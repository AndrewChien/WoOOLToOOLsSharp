using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

/// <summary>
/// DockBuilder API bindings.
/// ImGui.NET 目前未暴露 DockBuilder 的高层封装，但 cimgui 已提供导出符号。
/// 这里补一个最小封装以支持旧版 MapEditor 的“Reset Layout”默认布局重建。
/// </summary>
public static class ImGuiDockBuilder
{
    public static void RemoveNode(uint nodeId)
        => ImGuiDockBuilderNative.igDockBuilderRemoveNode(nodeId);

    public static void RemoveNodeDockedWindows(uint nodeId)
        => ImGuiDockBuilderNative.igDockBuilderRemoveNodeDockedWindows(nodeId, clearSettingsRefs: true);

    public static void RemoveNodeChildNodes(uint nodeId)
        => ImGuiDockBuilderNative.igDockBuilderRemoveNodeChildNodes(nodeId);

    public static void AddNode(uint nodeId, ImGuiDockNodeFlags flags)
        => ImGuiDockBuilderNative.igDockBuilderAddNode(nodeId, flags);

    public static void SetNodeSize(uint nodeId, Vector2 size)
        => ImGuiDockBuilderNative.igDockBuilderSetNodeSize(nodeId, size);

    public static uint SplitNode(uint nodeId, ImGuiDir splitDir, float sizeRatioForNodeAtDir, out uint outIdAtDir, out uint outIdAtOppositeDir)
        => ImGuiDockBuilderNative.igDockBuilderSplitNode(nodeId, splitDir, sizeRatioForNodeAtDir, out outIdAtDir, out outIdAtOppositeDir);

    public static unsafe void DockWindow(string windowName, uint nodeId)
    {
        if (windowName is null) throw new ArgumentNullException(nameof(windowName));

        int byteCount = Encoding.UTF8.GetByteCount(windowName);
        byte* utf8 = stackalloc byte[byteCount + 1];
        Encoding.UTF8.GetBytes(windowName.AsSpan(), new Span<byte>(utf8, byteCount));
        utf8[byteCount] = 0;
        ImGuiDockBuilderNative.igDockBuilderDockWindow(utf8, nodeId);
    }

    public static void Finish(uint nodeId)
        => ImGuiDockBuilderNative.igDockBuilderFinish(nodeId);

    private static unsafe class ImGuiDockBuilderNative
    {
        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderRemoveNode")]
        public static extern void igDockBuilderRemoveNode(uint node_id);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderRemoveNodeDockedWindows")]
        public static extern void igDockBuilderRemoveNodeDockedWindows(uint node_id, bool clearSettingsRefs);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderRemoveNodeChildNodes")]
        public static extern void igDockBuilderRemoveNodeChildNodes(uint node_id);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderAddNode")]
        public static extern void igDockBuilderAddNode(uint node_id, ImGuiDockNodeFlags flags);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSetNodeSize")]
        public static extern void igDockBuilderSetNodeSize(uint node_id, Vector2 size);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSplitNode")]
        public static extern uint igDockBuilderSplitNode(
            uint node_id,
            ImGuiDir split_dir,
            float size_ratio_for_node_at_dir,
            out uint out_id_at_dir,
            out uint out_id_at_opposite_dir);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderDockWindow")]
        public static extern void igDockBuilderDockWindow(byte* window_name, uint node_id);

        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderFinish")]
        public static extern void igDockBuilderFinish(uint node_id);
    }
}

