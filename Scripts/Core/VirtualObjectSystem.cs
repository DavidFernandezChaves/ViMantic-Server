﻿using UnityEngine;
using ROSUnityCore.ROSBridgeLib.ViMantic_msgs;
using ROSUnityCore;
using System.Linq;
using System.IO;
using System.Collections;
using System.Collections.Generic;

public class VirtualObjectSystem : MonoBehaviour {
    
    public static VirtualObjectSystem instance;
    public int verbose;
    
    public float threshold_match = 1f;
    public int minPixelsMask = 1000;

    public GameObject prefDetectedObject;
    public Transform tfFrameForObjects;
    public Camera bbCamera;

    public int nDetections { get; private set; }
    public List<SemanticObject> virtualSemanticMap { get; private set; }
    public Dictionary<Color32, VirtualObjectBox> boxColors { get; private set; }

    private Queue<DetectionArrayMsg> processingQueue;

    #region Unity Functions
    private void Awake() {
        if (!instance) {
            instance = this;
            DontDestroyOnLoad(gameObject);
        } else {
            Destroy(gameObject);
        }

        virtualSemanticMap = new List<SemanticObject>();
        boxColors = new Dictionary<Color32, VirtualObjectBox>();
        processingQueue = new Queue<DetectionArrayMsg>();
        StartCoroutine(ProcessMsgs());
    }
    #endregion

    #region Public Functions
    public void Connected(ROS ros) {
        ros.RegisterSubPackage("Vimantic_Detections_sub");
    }

    public void DetectedObject(DetectionArrayMsg _detections, string _ip) {
        processingQueue.Enqueue(_detections);
    }
        

    public Color32 GetColorObject(VirtualObjectBox vob) {
        Color32 newColor = new Color32((byte)Random.Range(0f, 255f), (byte)Random.Range(0f, 255f), (byte)Random.Range(0f, 255f), 255);
        while (boxColors.ContainsKey(newColor)) {
            newColor = new Color32((byte)Random.Range(0f, 255f), (byte)Random.Range(0f, 255f), (byte)Random.Range(0f, 255f), 255);
        }
        boxColors[newColor]=vob;
        return newColor;
    }

    public void UnregisterColor(Color32 color) {
        if (boxColors.ContainsKey(color)) {
            boxColors.Remove(color);
        }
    }
    #endregion

    #region Private Functions
    private IEnumerator ProcessMsgs() {

        Rect rect = new Rect(0, 0, bbCamera.pixelWidth, bbCamera.pixelHeight);
        Texture2D image = new Texture2D(bbCamera.pixelWidth, bbCamera.pixelHeight, TextureFormat.RGB24, false);

        while (Application.isPlaying) {

            if (processingQueue.Count > 0) {

                DetectionArrayMsg _detections = processingQueue.Dequeue();

                //Get view previous detections from bbCamera located in the origin
                bbCamera.transform.position = _detections.origin.GetPositionUnity();
                bbCamera.transform.rotation = _detections.origin.GetRotationUnity() * Quaternion.Euler(0f, 90f, 0f);

                
                
                Dictionary<VirtualObjectBox, int> virtualObjectBoxInRange = new Dictionary<VirtualObjectBox, int>();


                int i = 0;
                while (i<10) {

                    RenderTexture renderTextureMask = new RenderTexture(bbCamera.pixelWidth, bbCamera.pixelHeight,0);
                    bbCamera.targetTexture = renderTextureMask;
                    bbCamera.Render();
                    RenderTexture.active = renderTextureMask;
                    image.ReadPixels(rect, 0, 0);
                    image.Apply();

                    var q = from x in image.GetPixels()
                            group x by x into g
                            let count = g.Count()
                            orderby count descending
                            select new { Value = g.Key, Count = count };

                    foreach (var xx in q) {

                        if (boxColors.ContainsKey(xx.Value)) {
                            var vob = boxColors[xx.Value];
                            vob.gameObject.SetActive(false);
                            virtualObjectBoxInRange.Add(vob, xx.Count);
                            //Debug.Log("Value: " + xx.Value + " Count: " + xx.Count);
                        }
                    }

                    bbCamera.targetTexture = null;
                    RenderTexture.active = null; //Clean
                    Destroy(renderTextureMask); //Free memory
                    if (q.Count() == 1) break;
                    i++;
                }          

                foreach (VirtualObjectBox vob in virtualObjectBoxInRange.Keys) {
                    vob.gameObject.SetActive(true);
                    
                }

                List<VirtualObjectBox> detectedVirtualObjectBox = new List<VirtualObjectBox>();
                foreach (DetectionMsg detection in _detections.detections) {


                    SemanticObject virtualObject = new SemanticObject(detection.GetScores(),
                                                                        detection.GetCorners(),
                                                                        detection.occluded_corners);

                    //Check the type object is in the ontology
                    if (!OntologySystem.instance.CheckInteresObject(virtualObject.Type)) {
                        Log(virtualObject.Type + " - detected but it is not in the ontology");
                        continue;
                    }

                    //Insertion detection into the ontology
                    virtualObject = OntologySystem.instance.AddNewDetectedObject(virtualObject);

                    //Build Ranking
                    VirtualObjectBox match = null;
                    foreach (VirtualObjectBox vob in virtualObjectBoxInRange.Keys) {

                        var order =  YNN(vob.semanticObject.Corners, virtualObject.Corners);                        
                        float distance = CalculateCornerDistance(vob.semanticObject.Corners, order, false);
                        
                        if (distance < threshold_match) {
                            virtualObject.SetNewCorners(order);
                            match = vob;
                            Debug.Log("Union: " + virtualObject.Id+ " con: " + vob.semanticObject.Id + ", por distancia: " + distance);
                            break;
                        } else { Debug.Log("NO Union: " + virtualObject.Id+ " con: " + vob.semanticObject.Id + ", por distancia: " + distance); }
                    }

                    //Match process
                    if (match != null) {
                        match.NewDetection(virtualObject);
                        detectedVirtualObjectBox.Add(match);
                    } else {
                        if (verbose > 2) {
                            Log("New object detected: " + virtualObject.ToString());
                        }
                        virtualSemanticMap.Add(virtualObject);
                        VirtualObjectBox nvob = InstanceNewSemanticObject(virtualObject);
                        virtualObjectBoxInRange.Add(nvob, minPixelsMask+1);
                        detectedVirtualObjectBox.Add(nvob);
                    }
                    nDetections++;
                }
                detectedVirtualObjectBox.ForEach(dvob => virtualObjectBoxInRange.Remove(dvob));

                foreach (KeyValuePair<VirtualObjectBox, int> o in virtualObjectBoxInRange) {
                    if (o.Value > minPixelsMask) o.Key.NewDetection(null);
                }
            }

            yield return null;
        }
    }

    private VirtualObjectBox GetObjectMatch(Color32 color) {
        foreach(KeyValuePair<Color32, VirtualObjectBox> pair in boxColors) {
            if(Mathf.Abs(color.r-pair.Key.r) 
                + Mathf.Abs(color.g - pair.Key.g)
                + Mathf.Abs(color.b - pair.Key.b) < 13f) {
                return pair.Value.GetComponent<VirtualObjectBox>();
            }
        }
        return null;
    }

    private VirtualObjectBox InstanceNewSemanticObject(SemanticObject _obj) {
        Transform obj_inst = Instantiate(prefDetectedObject, _obj.Position, _obj.Rotation).transform;
        obj_inst.parent = tfFrameForObjects;
        VirtualObjectBox result = obj_inst.GetComponentInChildren<VirtualObjectBox>();
        result.InitializeSemanticObject(_obj);
        return result;
    }

    private void Log(string _msg) {
        if (verbose > 1)
            Debug.Log("[Object Manager]: " + _msg);
    }

    private void LogWarning(string _msg) {
        if (verbose > 0)
            Debug.LogWarning("[Object Manager]: " + _msg);
    }
    #endregion

    #region Static Functions
    static public List<SemanticObject.Corner> YNN(List<SemanticObject.Corner> reference, List<SemanticObject.Corner> observation) {

        Queue<SemanticObject.Corner> top = new Queue<SemanticObject.Corner>();
        top.Enqueue(observation[2]);
        top.Enqueue(observation[5]);
        top.Enqueue(observation[4]);
        top.Enqueue(observation[7]);
        Queue<SemanticObject.Corner> bottom = new Queue<SemanticObject.Corner>();
        bottom.Enqueue(observation[0]);
        bottom.Enqueue(observation[3]);
        bottom.Enqueue(observation[6]);
        bottom.Enqueue(observation[1]);

        int index = 0;
        float best_distance = Vector3.Distance(reference[2].position, top.ElementAt(0).position) +
                            Vector3.Distance(reference[4].position, top.ElementAt(1).position) +
                            Vector3.Distance(reference[5].position, top.ElementAt(2).position) +
                            Vector3.Distance(reference[7].position, top.ElementAt(3).position);

        for (int i = 1; i < 4; i++) {
            top.Enqueue(top.Dequeue());
            float distance = Vector3.Distance(reference[2].position, top.ElementAt(0).position) +
                                Vector3.Distance(reference[4].position, top.ElementAt(1).position) +
                                Vector3.Distance(reference[5].position, top.ElementAt(2).position) +
                                Vector3.Distance(reference[7].position, top.ElementAt(3).position);

            if (best_distance > distance) {
                index = i;
                best_distance = distance;
            }
        }

        top.Enqueue(top.Dequeue());

        for (int i = 0; i < index; i++) {
            top.Enqueue(top.Dequeue());
            bottom.Enqueue(bottom.Dequeue());
        }

        List<SemanticObject.Corner> result = new List<SemanticObject.Corner> {
            bottom.ElementAt(0),
            bottom.ElementAt(3),
            top.ElementAt(0),
            bottom.ElementAt(1),
            top.ElementAt(2),
            top.ElementAt(1),
            bottom.ElementAt(2),
            top.ElementAt(3)
        };

        return result;
    }

    static public float CalculateCornerDistance(List<SemanticObject.Corner> reference, List<SemanticObject.Corner> observation, bool onlyNonOccluded) {
        float distance = 0;
        //for (int i = 0; i < reference.Count; i++) {
        //    if ((!observation[i].occluded && !reference[i].occluded) || !onlyNonOccluded) {
        //        distance += Vector3.Distance(reference[i].position, observation[i].position);
        //    }
        //}

        if (distance == 0) {
            for (int i = 0; i < reference.Count; i++) {
                distance += Vector3.Distance(reference[i].position, observation[i].position);
            }
        }

        return distance == 0 ? 999 : distance;
    }
    #endregion

}