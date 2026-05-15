using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;



public class HizInstDebugController : MonoBehaviour
{
    [Header("多物体 GPU Instance 测试配置")]
    public Transform camTrans;
    
    [Header("在这里添加你要测试的各种物体组合")]
    public List<InstanceConfig> Configs = new List<InstanceConfig>();

    [Header("UI 按钮大小")]
    public Vector2 buttonSize = new Vector2(220, 50);

    // 用于管理当前生成的所有批次
    private List<HizInstanceBatch> m_Batches = new List<HizInstanceBatch>();

    private void Start()
    {
        Application.targetFrameRate = 120;
        GraphicsSettings.useScriptableRenderPipelineBatching = false;
        
        if (camTrans == null) camTrans = Camera.main.transform;
    }

    private void OnGUI()
    {
        float x = 20;
        float y = 20;
        float spacing = 10;
        GUIStyle btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 18 };

        // --- Hi-Z 开关 ---
        bool isEnabled = HizCullingMgr.Instance.IsEnable;
        GUI.backgroundColor = isEnabled ? Color.green : Color.red;
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), isEnabled ? "Hi-Z: 开启中" : "Hi-Z: 已关闭", btnStyle))
        {
            if (isEnabled) HizCullingMgr.Instance.Close();
            else HizCullingMgr.Instance.Open();
        }

        // 统计总生成数量
        int totalToSpawn = 0;
        foreach (var cfg in Configs) totalToSpawn += cfg.SpawnCount;

        // --- 生成多批次 Instance ---
        y += buttonSize.y + spacing;
        GUI.backgroundColor = Color.cyan;
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), $"生成 {totalToSpawn} 个 GPU 实例", btnStyle))
        {
            SpawnGPUInstances();
        }

        // --- 清空实例 ---
        y += buttonSize.y + spacing;
        GUI.backgroundColor = Color.white;
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), "清空所有实例", btnStyle))
        {
            ClearGPUInstances();
        }
        
        // --- 状态显示 ---
        y += buttonSize.y + spacing;
        GUI.Label(new Rect(x, y, 300, 30), $"当前运行批次 (Batch) 数量: {m_Batches.Count}", new GUIStyle { fontSize = 16, normal = new GUIStyleState { textColor = Color.white } });
    }

    private void SpawnGPUInstances()
    {
        ClearGPUInstances(); // 先清空旧的

        if (Configs == null || Configs.Count == 0)
        {
            Debug.LogWarning("配置列表为空！请在 Inspector 面板中添加 Configs。");
            return;
        }

        var camForward = camTrans.forward;
        var camPos = camTrans.position;
        int totalSpawned = 0;

        // 遍历所有配置的种类
        foreach (var config in Configs)
        {
            if (config.InstanceMesh == null || config.InstanceMaterial == null)
            {
                Debug.LogWarning($"[{config.Name}] 的 Mesh 或 Material 未配置，跳过生成。");
                continue;
            }

            // 为当前种类生成纯数据矩阵
            Matrix4x4[] matrices = new Matrix4x4[config.SpawnCount];
            for (int i = 0; i < config.SpawnCount; i++)
            {
                // 在相机前方扇形区域生成
                Vector3 randomDir = (camForward + Random.insideUnitSphere * 1.5f).normalized;
                float dist = Random.Range(10, config.SpawnRadius);
                Vector3 spawnPos = camPos + randomDir * dist;
                spawnPos.y = 0; // 铺在地上方便观察

                Quaternion rot = Quaternion.Euler(0, Random.Range(0, 360), 0);
                Vector3 scale = Vector3.one * Random.Range(config.MinScale, config.MaxScale);
                
                matrices[i] = Matrix4x4.TRS(spawnPos, rot, scale);
            }

            // 为当前种类创建专属的 GPU Batch
            var batch = new HizInstanceBatch(config.InstanceMesh, config.InstanceMaterial, matrices);
            
            // 交给管理器调度
            HizCullingMgr.Instance.AddInstanceBatch(batch);
            
            // 测试脚本自己也要存一份，方便等会清理
            m_Batches.Add(batch);
            totalSpawned += config.SpawnCount;
        }
        
        Debug.Log($"<color=cyan>成功生成 {totalSpawned} 个 GPU 实例，共 {m_Batches.Count} 个 Batch。</color>");
    }

    private void ClearGPUInstances()
    {
        if (m_Batches.Count > 0)
        {
            foreach (var batch in m_Batches)
            {
                // 1. 从管理器中剔除
                HizCullingMgr.Instance.GetInstanceBatches().Remove(batch);
                // 2. 释放 GPU 显存 Buffer
                batch.Dispose();
            }
            m_Batches.Clear();
            Debug.Log("<color=white>GPU 实例已全部清空并释放显存</color>");
        }
    }

    private void OnDestroy() 
    { 
        // 保证在退出游戏时不会发生显存泄漏
        ClearGPUInstances(); 
    }
}
// 序列化配置类：用于在面板上配置多种不同的物体
[System.Serializable]
public class InstanceConfig
{
    public string Name = "物体种类 (如: 树/草/石头)";
    public Mesh InstanceMesh;
    public Material InstanceMaterial; // 【必须】使用支持 IndirectBuffer 的那个 Shader
    
    public int SpawnCount = 10000;   // 这个种类的生成数量
    public float SpawnRadius = 200f; // 分布半径
    public float MinScale = 0.5f;    // 最小缩放
    public float MaxScale = 2.0f;    // 最大缩放
}