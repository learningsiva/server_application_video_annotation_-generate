using UnityEngine;
using System;

[System.Serializable]
public class AnnotationData
{
    public string id;
    public int videoAnnotationId; // Maps to video_annotation_id
    public float timestamp;       // Maps to timestamps
    public BoundingBoxData boundingBox;
    public string[] annotations;

    [System.Serializable]
    public class BoundingBoxData
    {
        public float normalizedX;
        public float normalizedY;
    }

    public float normalizedX => boundingBox?.normalizedX ?? 0f;
    public float normalizedY => boundingBox?.normalizedY ?? 0f;

    public Vector2 GetScreenPosition(RectTransform videoDisplayRect)
    {
        if (videoDisplayRect == null || boundingBox == null) return Vector2.zero;
        Rect rect = videoDisplayRect.rect;
        float rectX = normalizedX * rect.width;
        float rectY = normalizedY * rect.height;
        return new Vector2(rectX - (rect.width / 2f), rectY - (rect.height / 2f));
    }

    public string GetFormattedTime()
    {
        return $"{Mathf.FloorToInt(timestamp / 60f):00}:{Mathf.FloorToInt(timestamp % 60f):00}";
    }
}