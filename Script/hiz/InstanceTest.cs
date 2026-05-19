
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
    public Vector2 buttonSize = new Vector2(240, 50);

    // 用于管理当前生成的所有批次
    private List<HizInstanceBatch> m_Batches = new List<HizInstanceBatch>();

    private void Start()
    {
        Application.targetFrameRate = 120;
        // 确保禁用 SRP Batcher 以免干扰底层 Buffer 绑定（根据你的实现而定）
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
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), isEnabled ? "Hi-Z + GPU LOD: 开启" : "Hi-Z: 已关闭", btnStyle))
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
        if (GUI.Button(new Rect(x, y, buttonSize.x, buttonSize.y), $"生成 {totalToSpawn} 个实例", btnStyle))
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
        var style = new GUIStyle { fontSize = 16 };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(x, y, 400, 30), $"当前 Batch 数量: {m_Batches.Count}", style);
        y += 25;
        GUI.Label(new Rect(x, y, 400, 30), $"显存占用 (仅矩阵): ~{(totalToSpawn * 112) / (1024f * 1024f):F2} MB", style);
    }

    private void SpawnGPUInstances()
    {
        ClearGPUInstances();

        if (Configs == null || Configs.Count == 0) return;

        var camForward = camTrans.forward;
        var camPos = camTrans.position;
        int totalSpawned = 0;

        foreach (var config in Configs)
        {
            // 准备 LOD 数组
            Mesh[] lodMeshes = new Mesh[3] { config.LOD0_Mesh, config.LOD1_Mesh, config.LOD2_Mesh };
            Material[] lodMats = new Material[3] { config.LOD0_Material, config.LOD1_Material, config.LOD2_Material };

            if (lodMeshes[0] == null || lodMats[0] == null) continue;

            Matrix4x4[] matrices = new Matrix4x4[config.SpawnCount];
            Vector4[] blocks = new Vector4[config.SpawnCount];

            for (int i = 0; i < config.SpawnCount; i++)
            {
                // 分布算法：在相机周围随机生成
                Vector2 randCircle = Random.insideUnitCircle * config.SpawnRadius;
                Vector3 spawnPos = camPos + new Vector3(randCircle.x, 0, randCircle.y);
                spawnPos.y = 0; 

                Quaternion rot = Quaternion.Euler(0, Random.Range(0, 360), 0);
                Vector3 scale = Vector3.one * Random.Range(config.MinScale, config.MaxScale);
                matrices[i] = Matrix4x4.TRS(spawnPos, rot, scale);

                // 生成随机 blockData (例如：x=随机颜色偏移, y=随机动画相位)
                blocks[i] = new Vector4(Random.value, Random.value, Random.value, Random.value);
            }

            // 构造新的多 LOD Batch
            // lodDistances 参数: x=LOD0距离, y=LOD1距离, z=LOD2距离, w=密度(1.0)
            Vector4 lodDistances = new Vector4(config.LOD0_Distance, config.LOD1_Distance, config.LOD2_Distance, 1.0f);
            
            // 包围盒大小以 LOD0 为准
            Vector3 extents = lodMeshes[0].bounds.extents;

            var batch = new HizInstanceBatch(
                lodMeshes, 
                lodMats, 
                lodDistances, 
                extents, 
                matrices, 
                blocks
            );
            
            HizCullingMgr.Instance.AddInstanceBatch(batch);
            m_Batches.Add(batch);
            totalSpawned += config.SpawnCount;
        }
        
        Debug.Log($"<color=cyan><b>[HizTest]</b></color> 已生成 {totalSpawned} 个实例。通过 GPU LOD 分流。");
    }

    private void ClearGPUInstances()
    {
        if (m_Batches.Count > 0)
        {
            // 清理管理器中的引用
            foreach (var batch in m_Batches)
            {
                HizCullingMgr.Instance.GetInstanceBatches().Remove(batch);
                batch.Dispose();
            }
            m_Batches.Clear();
            Debug.Log("已清理所有实例");
        }
    }

    private void OnDestroy() { ClearGPUInstances(); }
}

[System.Serializable]
public class InstanceConfig
{
    public string Name = "新物体";
    
    [Header("LOD 0 (高模)")]
    public Mesh LOD0_Mesh;
    public Material LOD0_Material;
    public float LOD0_Distance = 30f;

    [Header("LOD 1 (中模)")]
    public Mesh LOD1_Mesh;
    public Material LOD1_Material;
    public float LOD1_Distance = 80f;

    [Header("LOD 2 (低模/告示板)")]
    public Mesh LOD2_Mesh;
    public Material LOD2_Material;
    public float LOD2_Distance = 200f;

    [Space]
    public int SpawnCount = 5000;
    public float SpawnRadius = 150f;
    public float MinScale = 0.8f;
    public float MaxScale = 1.2f;
}