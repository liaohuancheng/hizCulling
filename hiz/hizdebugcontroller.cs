using System;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

public class HizDebugController : MonoBehaviour
{
    [Header("测试配置")]
    public int spawnCount = 2000;
    public float spawnRange = 100f;
    public Vector2 buttonSize = new Vector2(220, 50);
    public GameObject prefab;
    private GameObject m_TestRoot;
    private List<GameObject> m_SpawnedObjects = new List<GameObject>();
    public Transform camTrans;

    private void Start()
    {
        Application.targetFrameRate = 120;
        GraphicsSettings.useScriptableRenderPipelineBatching = false;
    }
    private void OnGUI()
    {
        float x = 20;
        float y = 20;
        float spacing = 10;

        GUIStyle btnStyle = new GUIStyle(GUI.skin.button);
        btnStyle.fontSize = 18;

        // --- 按钮 1: Hi-Z 开关 ---
        bool isEnabled = HizCullingMgr.Instance.IsEnable;
        GUI.backgroundColor = isEnabled ? Color.green : Color.red;
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), isEnabled ? "Hi-Z: 开启中" : "Hi-Z: 已关闭", btnStyle))
        {
            if (isEnabled) HizCullingMgr.Instance.Close();
            else HizCullingMgr.Instance.Open();
        }

        // --- 按钮 2: 生成 2000 个测试 Cube ---
        y += buttonSize.y + spacing;
        GUI.backgroundColor = Color.cyan;
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), $"生成 {spawnCount} 个测试物体", btnStyle))
        {
            SpawnTestCubes();
        }

        // --- 按钮 3: 清空测试物体 ---
        y += buttonSize.y + spacing;
        GUI.backgroundColor = Color.white;
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), "清空测试物体", btnStyle))
        {
            ClearTestCubes();
        }
        
        y += buttonSize.y + spacing;
        GUI.backgroundColor = Color.white;
        var text = HizCullingMgr.Instance.getR8() ? "关闭R8" : "开启R8";
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), text, btnStyle))
        {
            HizCullingMgr.Instance.SetR8();
        }

        // 状态显示
        y += buttonSize.y + spacing;
        GUI.Label(new Rect(x, y, 300, 30), $"当前物体总数: {m_SpawnedObjects.Count}", new GUIStyle { fontSize = 16, normal = new GUIStyleState { textColor = Color.white } });
    }

    
    private void SpawnTestCubes()
    {
        if (m_TestRoot == null)
        {
            m_TestRoot = new GameObject("Hiz_Test_Root");
        }
        var camForward = camTrans.forward;
        var camPos = camTrans.position;
        
        // 随机生成
        for (int i = 0; i < spawnCount; i++)
        {
            // 1. 在相机前方的一个锥形/球壳区域内随机
            // 这样可以确保物体分布在“远、中、近”不同的深度层级，测试 Hi-Z 效果
            Vector3 randomDir = (camForward + Random.insideUnitSphere * 0.5f).normalized;
            float dist = Random.Range(10, 500);
            Vector3 spawnPos = camPos + randomDir * dist;

            // 2. 实例化
            var cube = Instantiate(prefab, spawnPos, Random.rotation, m_TestRoot.transform);
            
            
            // 随机缩放 (增加测试多样性)
            cube.transform.localScale = Vector3.one * Random.Range(0.5f, 3.0f);
            
            m_SpawnedObjects.Add(cube);
        }
        
        Debug.Log($"<color=cyan>成功生成 {spawnCount} 个测试物体</color>");
    }

    private void ClearTestCubes()
    {
        foreach (var obj in m_SpawnedObjects)
        {
            if (obj != null) Destroy(obj);
        }
        m_SpawnedObjects.Clear();
        if (m_TestRoot != null) Destroy(m_TestRoot);
        
        Debug.Log("<color=white>测试物体已全部清空</color>");
    }
}