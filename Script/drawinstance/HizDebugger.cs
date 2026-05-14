using UnityEngine;
using UnityEngine.Rendering;

public class HizCullingDebugger : MonoBehaviour
{
    // 引用你的 Batch 对象
    // 如果你在 HizCullingMgr 里维护列表，可以改为从 Mgr 获取
    public HizInstanceTest testScript; 

    private float m_Timer = 0f;

    void Update()
    {
        m_Timer += Time.deltaTime;
        
        // 每 0.5 秒回读一次，避免频繁回读影响性能
        if (m_Timer > 0.5f)
        {
            m_Timer = 0f;
            ReadbackCount();
        }
    }

    void ReadbackCount()
    {
        // 假设你的 HizInstanceBatch 存储在 testScript.m_Batch
        // 这里需要根据你的实际变量名调整
        var batch = HizCullingMgr.Instance.GetInstanceBatches();
        if (batch == null ) return;

        var targetBatch = batch[0]; // 测试第一个 Batch

        // 请求回读 ArgsBuffer
        // 参数：buffer, size (20字节), offset (0), callback
        AsyncGPUReadback.Request(targetBatch.argsBuffer, 20, 0, (AsyncGPUReadbackRequest request) => {
            if (request.hasError)
            {
                Debug.LogError("GPU 回读失败");
                return;
            }

            // 获取数据
            var data = request.GetData<uint>();
            
            // ArgsBuffer 布局:
            // [0] indexCountPerInstance
            // [1] instanceCount <--- 我们要的
            // [2] startIndexLocation
            // [3] baseVertexLocation
            // [4] startInstanceLocation

            uint indexCount = data[0];
            uint instanceCount = data[1];

            Debug.Log($"<color=#00FF00>[Hiz Debug]</color> " +
                      $"单实例索引数: {indexCount} | " +
                      $"<b>当前可见实例数: {instanceCount}</b> / 总数: {targetBatch.totalCount}");
        });
    }
}