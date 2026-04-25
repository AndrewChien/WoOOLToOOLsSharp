using System;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

public interface IVulkanApp : IDisposable
{
    void ConfigureImGui(VulkanRenderer renderer, ImGuiController controller);
    void Tick(GlfwInput input, float deltaSeconds);
    bool RequestExit { get; }
}
