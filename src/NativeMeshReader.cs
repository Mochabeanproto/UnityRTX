using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    /// <summary>
    /// Reads mesh vertex/index data from GPU buffers via D3D11 COM interop.
    /// Uses Mesh.GetNativeVertexBufferPtr/GetNativeIndexBufferPtr (available in Unity 2019)
    /// to get the ID3D11Buffer*, then copies to a staging buffer for CPU readback.
    /// This bypasses the isReadable limitation — the GPU buffers exist for ALL meshes
    /// (they're needed for rendering), even when CPU-side data has been freed.
    /// </summary>
    internal static class NativeMeshReader
    {
        // D3D11 vtable slot indices (IUnknown=0-2, ID3D11DeviceChild=3-6, ID3D11Resource=7-9)
        const int SLOT_Release = 2;
        const int SLOT_GetDevice = 3;           // ID3D11DeviceChild::GetDevice
        const int SLOT_BufferGetDesc = 10;       // ID3D11Buffer::GetDesc
        const int SLOT_CreateBuffer = 3;         // ID3D11Device::CreateBuffer
        const int SLOT_GetImmediateContext = 40; // ID3D11Device::GetImmediateContext
        const int SLOT_Map = 14;                 // ID3D11DeviceContext::Map
        const int SLOT_Unmap = 15;               // ID3D11DeviceContext::Unmap
        const int SLOT_CopyResource = 47;        // ID3D11DeviceContext::CopyResource

        static ManualLogSource logger;
        static bool loggedInit;

        public static void SetLogger(ManualLogSource log) => logger = log;

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_BUFFER_DESC
        {
            public uint ByteWidth;
            public uint Usage;       // 0=DEFAULT, 3=STAGING
            public uint BindFlags;
            public uint CPUAccessFlags;
            public uint MiscFlags;
            public uint StructureByteStride;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch;
            public uint DepthPitch;
        }

        // COM vtable call delegates
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate uint ReleaseD(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void GetDeviceD(IntPtr self, out IntPtr ppDevice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void BufferGetDescD(IntPtr self, out D3D11_BUFFER_DESC pDesc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void GetImmediateContextD(IntPtr self, out IntPtr ppContext);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreateBufferD(IntPtr self, ref D3D11_BUFFER_DESC pDesc, IntPtr pInitialData, out IntPtr ppBuffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void CopyResourceD(IntPtr self, IntPtr pDst, IntPtr pSrc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int MapD(IntPtr self, IntPtr pResource, uint subresource, uint mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE pMapped);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void UnmapD(IntPtr self, IntPtr pResource, uint subresource);

        static T VTable<T>(IntPtr comObj, int slot) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObj);
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        /// <summary>
        /// Read raw bytes from a D3D11 buffer (vertex or index) via staging buffer copy.
        /// The nativeBuffer is an ID3D11Buffer* obtained from GetNativeVertexBufferPtr/GetNativeIndexBufferPtr.
        /// </summary>
        public static bool ReadBuffer(IntPtr nativeBuffer, out byte[] data)
        {
            data = null;
            if (nativeBuffer == IntPtr.Zero)
                return false;

            IntPtr device = IntPtr.Zero;
            IntPtr context = IntPtr.Zero;
            IntPtr staging = IntPtr.Zero;

            try
            {
                // Query buffer size
                var getDesc = VTable<BufferGetDescD>(nativeBuffer, SLOT_BufferGetDesc);
                getDesc(nativeBuffer, out var desc);

                if (desc.ByteWidth == 0)
                    return false;

                // Get D3D11 device from the buffer
                var getDevice = VTable<GetDeviceD>(nativeBuffer, SLOT_GetDevice);
                getDevice(nativeBuffer, out device); // AddRefs
                if (device == IntPtr.Zero)
                    return false;

                // Get immediate context
                var getCtx = VTable<GetImmediateContextD>(device, SLOT_GetImmediateContext);
                getCtx(device, out context); // AddRefs
                if (context == IntPtr.Zero)
                    return false;

                if (!loggedInit)
                {
                    logger?.LogInfo($"[NativeMeshReader] D3D11 device={device:X}, context={context:X}, buffer size={desc.ByteWidth}");
                    loggedInit = true;
                }

                // Create a staging buffer for CPU readback
                var stagingDesc = new D3D11_BUFFER_DESC
                {
                    ByteWidth = desc.ByteWidth,
                    Usage = 3,              // D3D11_USAGE_STAGING
                    BindFlags = 0,
                    CPUAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
                    MiscFlags = 0,
                    StructureByteStride = 0
                };

                var createBuf = VTable<CreateBufferD>(device, SLOT_CreateBuffer);
                int hr = createBuf(device, ref stagingDesc, IntPtr.Zero, out staging);
                if (hr < 0 || staging == IntPtr.Zero)
                {
                    logger?.LogWarning($"[NativeMeshReader] CreateBuffer(staging) failed: 0x{hr:X8}");
                    return false;
                }

                // GPU → staging copy
                var copyRes = VTable<CopyResourceD>(context, SLOT_CopyResource);
                copyRes(context, staging, nativeBuffer);

                // Map staging buffer for CPU read
                var map = VTable<MapD>(context, SLOT_Map);
                hr = map(context, staging, 0, 1 /* D3D11_MAP_READ */, 0, out var mapped);
                if (hr < 0)
                {
                    logger?.LogWarning($"[NativeMeshReader] Map(staging) failed: 0x{hr:X8}");
                    return false;
                }

                try
                {
                    data = new byte[desc.ByteWidth];
                    Marshal.Copy(mapped.pData, data, 0, (int)desc.ByteWidth);
                }
                finally
                {
                    var unmap = VTable<UnmapD>(context, SLOT_Unmap);
                    unmap(context, staging, 0);
                }

                return true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"[NativeMeshReader] Exception: {ex.Message}");
                return false;
            }
            finally
            {
                // Release COM objects we acquired (GetDevice/GetImmediateContext/CreateBuffer all AddRef)
                if (staging != IntPtr.Zero)
                    VTable<ReleaseD>(staging, SLOT_Release)(staging);
                if (context != IntPtr.Zero)
                    VTable<ReleaseD>(context, SLOT_Release)(context);
                if (device != IntPtr.Zero)
                    VTable<ReleaseD>(device, SLOT_Release)(device);
                // Do NOT release nativeBuffer — we don't own it (Unity does)
            }
        }

        /// <summary>
        /// Read mesh vertex and index data via native D3D11 buffer readback.
        /// Works for non-readable meshes where mesh.vertices would throw/return empty.
        /// </summary>
        public static bool ReadMesh(Mesh mesh, int stride,
            int posOffset, VertexAttributeFormat posFormat,
            int normOffset, VertexAttributeFormat normFormat,
            int uvOffset, VertexAttributeFormat uvFormat,
            out Vector3[] positions, out Vector3[] normals, out Vector2[] uvs,
            out int[][] subMeshIndices)
        {
            positions = null;
            normals = null;
            uvs = null;
            subMeshIndices = null;

            int vertexCount = mesh.vertexCount;
            if (vertexCount == 0 || stride == 0)
                return false;

            // Read vertex buffer via native pointer
            IntPtr nativeVB = mesh.GetNativeVertexBufferPtr(0);
            if (!ReadBuffer(nativeVB, out byte[] rawVerts))
            {
                logger?.LogWarning($"[NativeMeshReader] Failed to read vertex buffer for '{mesh.name}'");
                return false;
            }

            // Validate size
            int expectedSize = vertexCount * stride;
            if (rawVerts.Length < expectedSize)
            {
                logger?.LogWarning($"[NativeMeshReader] Vertex buffer too small: {rawVerts.Length} < {expectedSize} for '{mesh.name}'");
                return false;
            }

            // Parse positions
            positions = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int off = i * stride + posOffset;
                positions[i] = ReadVector3(rawVerts, off, posFormat);
            }

            // Parse normals
            if (normOffset >= 0)
            {
                normals = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    int off = i * stride + normOffset;
                    normals[i] = ReadVector3(rawVerts, off, normFormat);
                }
            }

            // Parse UVs
            if (uvOffset >= 0)
            {
                uvs = new Vector2[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    int off = i * stride + uvOffset;
                    uvs[i] = ReadVector2(rawVerts, off, uvFormat);
                }
            }

            // Read index buffer
            IntPtr nativeIB = mesh.GetNativeIndexBufferPtr();
            if (!ReadBuffer(nativeIB, out byte[] rawIdx))
            {
                logger?.LogWarning($"[NativeMeshReader] Failed to read index buffer for '{mesh.name}'");
                return false;
            }

            bool is32Bit = mesh.indexFormat == IndexFormat.UInt32;
            int indexStride = is32Bit ? 4 : 2;

            // Split into per-submesh arrays
            int totalIndices = 0;
            var subList = new System.Collections.Generic.List<int[]>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                {
                    subList.Add(null);
                    continue;
                }

                var desc = mesh.GetSubMesh(sub);
                int start = desc.indexStart;
                int count = desc.indexCount;
                var tris = new int[count];

                for (int i = 0; i < count; i++)
                {
                    int byteOff = (start + i) * indexStride;
                    tris[i] = is32Bit
                        ? BitConverter.ToInt32(rawIdx, byteOff)
                        : BitConverter.ToUInt16(rawIdx, byteOff);
                }

                subList.Add(tris);
                totalIndices += count;
            }

            subMeshIndices = subList.ToArray();

            logger?.LogInfo($"[NativeMeshReader] '{mesh.name}' — {vertexCount} verts, {totalIndices} indices, {mesh.subMeshCount} submeshes (D3D11 readback)");
            return positions.Length > 0 && totalIndices > 0;
        }

        static Vector3 ReadVector3(byte[] buf, int offset, VertexAttributeFormat fmt)
        {
            if (fmt == VertexAttributeFormat.Float32)
            {
                return new Vector3(
                    BitConverter.ToSingle(buf, offset),
                    BitConverter.ToSingle(buf, offset + 4),
                    BitConverter.ToSingle(buf, offset + 8));
            }
            if (fmt == VertexAttributeFormat.Float16)
            {
                return new Vector3(
                    HalfToFloat(BitConverter.ToUInt16(buf, offset)),
                    HalfToFloat(BitConverter.ToUInt16(buf, offset + 2)),
                    HalfToFloat(BitConverter.ToUInt16(buf, offset + 4)));
            }
            return Vector3.zero;
        }

        static Vector2 ReadVector2(byte[] buf, int offset, VertexAttributeFormat fmt)
        {
            if (fmt == VertexAttributeFormat.Float32)
            {
                return new Vector2(
                    BitConverter.ToSingle(buf, offset),
                    BitConverter.ToSingle(buf, offset + 4));
            }
            if (fmt == VertexAttributeFormat.Float16)
            {
                return new Vector2(
                    HalfToFloat(BitConverter.ToUInt16(buf, offset)),
                    HalfToFloat(BitConverter.ToUInt16(buf, offset + 2)));
            }
            return Vector2.zero;
        }

        static float HalfToFloat(ushort half)
        {
            int sign = (half >> 15) & 1;
            int exp = (half >> 10) & 0x1F;
            int mantissa = half & 0x3FF;

            if (exp == 0)
            {
                if (mantissa == 0) return sign == 1 ? -0f : 0f;
                // Subnormal
                float val = mantissa / 1024f * (1f / 16384f);
                return sign == 1 ? -val : val;
            }
            if (exp == 0x1F)
            {
                return mantissa == 0
                    ? (sign == 1 ? float.NegativeInfinity : float.PositiveInfinity)
                    : float.NaN;
            }

            float result = (float)((1.0 + mantissa / 1024.0) * Math.Pow(2, exp - 15));
            return sign == 1 ? -result : result;
        }
    }
}
