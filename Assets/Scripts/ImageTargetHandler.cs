using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

[RequireComponent(typeof(ARTrackedImageManager))]
public class ImageTargetHandler : MonoBehaviour
{
    [SerializeField]
    private GameObject infoPrefab;

    private ARTrackedImageManager trackedImageManager;
    private Dictionary<string, GameObject> spawnedObjects = new Dictionary<string, GameObject>();

    void Awake()
    {
        trackedImageManager = GetComponent<ARTrackedImageManager>();
    }

    void OnEnable()
    {
        trackedImageManager.trackablesChanged.AddListener(OnChanged);
    }

    void OnDisable()
    {
        trackedImageManager.trackablesChanged.RemoveListener(OnChanged);
    }

    void OnChanged(ARTrackablesChangedEventArgs<ARTrackedImage> eventArgs)
    {
        foreach (var trackedImage in eventArgs.added)
        {
            SpawnOrUpdate(trackedImage);
        }

        foreach (var trackedImage in eventArgs.updated)
        {
            SpawnOrUpdate(trackedImage);
        }

        foreach (var pair in eventArgs.removed)
        {
            var trackedImage = pair.Value;
            if (spawnedObjects.ContainsKey(trackedImage.referenceImage.name))
            {
                Destroy(spawnedObjects[trackedImage.referenceImage.name]);
                spawnedObjects.Remove(trackedImage.referenceImage.name);
            }
        }
    }

    void SpawnOrUpdate(ARTrackedImage trackedImage)
    {
        string imageName = trackedImage.referenceImage.name;

        if (!spawnedObjects.ContainsKey(imageName))
        {
            GameObject newObject = Instantiate(infoPrefab, trackedImage.transform.position, trackedImage.transform.rotation);
            spawnedObjects[imageName] = newObject;
        }

        GameObject obj = spawnedObjects[imageName];
        obj.transform.position = trackedImage.transform.position;
        obj.transform.rotation = trackedImage.transform.rotation;
        obj.SetActive(trackedImage.trackingState == TrackingState.Tracking);
    }
}