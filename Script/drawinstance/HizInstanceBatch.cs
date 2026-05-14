// using UnityEngine;
//
// public class HizInstanceBatch
// {
//     public Mesh mesh;
//     public Material material;
//     
//     // 1. 原始数据：所有实例的变换矩阵和 AABB
//     public ComputeBuffer instanceDataBuffer; // 存储 Matrix4x4 或自定义结构体
//     // 2. 结果数据：可见实例的索引 (AppendBuffer)
//     public ComputeBuffer visibleIndexBuffer; 
//     // 3. 间接绘制参数：[IndexCount, InstanceCount, StartIndex, BaseVertex, StartInstance]
//     public ComputeBuffer argsBuffer;
//
//     private uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
//     public int totalCount;
//
//     public HizInstanceBatch(Mesh mesh, Material material, Matrix4x4[] matrices)
//     {
//         this.mesh = mesh;
//         this.material = material;
//         this.totalCount = matrices.Length;
//
//         // 初始化 Buffer
//         instanceDataBuffer = new ComputeBuffer(totalCount, 16 * 4); // Matrix4x4
//         instanceDataBuffer.SetData(matrices);
//
//         // AppendBuffer：存储可见物体的 uint 索引
//         visibleIndexBuffer = new ComputeBuffer(totalCount, sizeof(uint), ComputeBufferType.Append);
//
//         // ArgsBuffer：DrawMeshInstancedIndirect 专用
//         argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
//         args[0] = mesh.GetIndexCount(0);
//         args[1] = 0; // 初始数量为 0，由 GPU 填充
//         args[2] = mesh.GetIndexStart(0);
//         args[3] = mesh.GetBaseVertex(0);
//         argsBuffer.SetData(args);
//     }
//
//     public void Release()
//     {
//         instanceDataBuffer?.Release();
//         visibleIndexBuffer?.Release();
//         argsBuffer?.Release();
//     }
// }