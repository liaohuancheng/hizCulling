using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
//[ExecuteInEditMode]
[RequireComponent(typeof(Renderer))]
public class HizCullableMono : MonoBehaviour , IHizCullable
{
    public Transform CachedTransform { get;
        set; }
    public int HizIndex { get; set; } = -1;
    private bool m_NeedsUpdate = true;
    private Vector3 m_CachedCenter;
    private Vector3 m_CachedExtent;
    private Bounds m_CachedBounds;
    private bool m_IsCull;
    private Renderer m_Renderer;

    // 当物体移动后，手动调用此方法，或者通过脚本在 Transform 改变时触发
    public void MarkBoundsDirty() {
        m_NeedsUpdate = true;
        UpdateCache();
        if (HizCullingMgr.Instance != null && HizCullingMgr.Instance.IsEnable) {
            HizCullingMgr.Instance.MarkDirty(this);
        }
    }
    public void UpdateCache() {
        // 确保每帧只更新一次
        if (!m_NeedsUpdate) return;

        // 性能瓶颈点：调用一次 Renderer.bounds
        m_CachedBounds = m_Renderer.bounds;
        m_CachedCenter = m_CachedBounds.center;
        m_CachedExtent = m_CachedBounds.extents;
        m_NeedsUpdate = false;

    }
    
    public Bounds GetWorldBounds()
    {
        UpdateCache();
        return m_CachedBounds;
    }
    public Vector3 GetWorldBoundsCenter()
    {
        UpdateCache();
        return m_CachedCenter;
    }
    public Vector3 GetWorldBoundsExtent()
    {
        UpdateCache();
        return m_CachedExtent;
    }
    private void OnEnable() {
        HizCullingMgr.Instance.AddCullable(this);
    }

    private void OnDisable() {
        HizCullingMgr.Instance.RemoveCullable(this);
    }
    void Awake() {
        m_Renderer = GetComponent<Renderer>();
        CachedTransform = transform;
        CachedTransform.hasChanged = false; // 初始化
        UpdateCache();
    }

    public void OnCulled() {
        if (!m_IsCull) {
            m_IsCull = true;
            m_Renderer.enabled = false;
        }
    }

    public void OnVisible() {
        if (m_IsCull) {
            m_Renderer.enabled = true;
            m_IsCull = false;
        }
    }
    public void SetLayer(bool isVisible)
    {
        if (isVisible)
        {
            
        }
        else
        {
            
        }
    }

    public bool IsCull() {
        return m_IsCull;
    }
    

}
