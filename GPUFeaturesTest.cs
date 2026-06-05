using UnityEngine;

public class GPUFeaturesTest : MonoBehaviour
{
    private string m_ResultText = "Detecting GPU features...";

    void Start()
    {
        // 1. 检测基础 GPU 实例化支持
        bool supportsInstancing = SystemInfo.supportsInstancing;
        
        // 2. 检测间接参数缓冲区支持（DMII的核心）
        bool supportsIndirectArgumentsBuffer = SystemInfo.supportsIndirectArgumentsBuffer;
        
        // 3. 检测计算着色器支持
        bool supportsComputeShaders = SystemInfo.supportsComputeShaders;

        // 综合评估前三项是否同时满足
        bool allThreeSupported = supportsInstancing && supportsIndirectArgumentsBuffer && supportsComputeShaders;

        // 4. 辅助检测：顶点着色器中可读取的 ComputeBuffer 最大数量
        // 如果此项为 0，在 OpenGL ES 下使用 StructuredBuffer 会导致无法渲染或报错
        int maxComputeInputsVertex = SystemInfo.maxComputeBufferInputsVertex;

        // 格式化输出文本
        m_ResultText = "【GPU Feature Test Results】\n\n" +
                       $"1. Supports Instancing: {supportsInstancing}\n" +
                       $"2. Supports Indirect Arguments Buffer: {supportsIndirectArgumentsBuffer}\n" +
                       $"3. Supports Compute Shaders: {supportsComputeShaders}\n\n" +
                       $"=> [Result] All Three Main Supported: {allThreeSupported}\n\n" +
                       $"----------------------------------------\n" +
                       $"[Additional Check for Mali GPU]\n" +
                       $"Max Compute Buffer Inputs (Vertex Shader): {maxComputeInputsVertex}\n" +
                       $"(If 0, StructuredBuffer in Vertex shader might fail on OpenGL ES)";

        // 输出到 Unity 控制台（如果是真机，可通过 adb logcat 查看）
        Debug.Log(m_ResultText);
    }

    void OnGUI()
    {
        // 为了方便在手机（如 vivo Y5s）的高分辨率屏幕上阅读，动态调整字体大小
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = Mathf.RoundToInt(Screen.height * 0.03f); // 字体大小根据屏幕高度自适应
        style.normal.textColor = Color.yellow;
        style.wordWrap = true;

        // 绘制半透明黑色背景框，确保文字清晰可读
        GUI.Box(new Rect(10, 10, Screen.width - 20, Screen.height * 0.6f), "");
        
        // 在屏幕上绘制检测结果
        GUI.Label(new Rect(30, 30, Screen.width - 60, Screen.height * 0.55f), m_ResultText, style);
    }
}