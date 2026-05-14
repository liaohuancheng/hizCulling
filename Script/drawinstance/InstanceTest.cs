using UnityEngine;

public class HizInstanceTest : MonoBehaviour {
    [Header("基础设置")]
    public Camera TargetCamera; // 参考相机
    public Mesh Mesh;           // 实例网格
    public Material Material;   // 必须是支持 Indirect 的 Shader

    [Header("生成参数")]
    public int Count = 10000;       // 生成数量
    public float SpawnRadius = 50f; // 环绕半径
    public float MinRadius = 5f;    // 最小半径（避免离相机太近）
    public float YOffset = -1.0f;   // 相对于相机初始位置的垂直偏移

    private HizInstanceBatch m_Batch;

    void Start() {
        if (TargetCamera == null) TargetCamera = Camera.main;
        
        // 获取相机初始位置
        Vector3 centerPos = TargetCamera.transform.position;
        
        Matrix4x4[] matrices = new Matrix4x4[Count];
        for (int i = 0; i < Count; i++) {
            // 在圆环内随机生成位置
            Vector2 randomCircle = Random.insideUnitCircle.normalized * Random.Range(MinRadius, SpawnRadius);
            
            // 计算最终世界坐标
            Vector3 pos = new Vector3(
                centerPos.x + randomCircle.x,
                centerPos.y + YOffset, 
                centerPos.z + randomCircle.y
            );

            // 随机旋转（可选，让场景看起来更自然）
            Quaternion rot = Quaternion.Euler(0, Random.Range(0, 360f), 0);
            
            matrices[i] = Matrix4x4.TRS(pos, rot, Vector3.one);
        }

        m_Batch = new HizInstanceBatch(Mesh, Material, matrices);
        // 将 Batch 注册到管理器中
        // 注意：确保你的 HizCullingMgr 有 AddInstanceBatch 方法
        HizCullingMgr.Instance.AddInstanceBatch(m_Batch);
    }

    void OnDestroy() {
        if (m_Batch != null) {
        }
    }
}
