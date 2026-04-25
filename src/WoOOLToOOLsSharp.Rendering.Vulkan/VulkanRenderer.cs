using System;
using System.Collections.Generic;
using ImGuiNET;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

public sealed unsafe class VulkanRenderer : IDisposable, IAsyncDisposable
{
    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";
    private const string DebugUtilsExtensionName = "VK_EXT_debug_utils";

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly string _appName;
    private readonly VulkanRendererOptions _options;
    private readonly int _maxFramesInFlight;

    private readonly GlfwCallbacks.FramebufferSizeCallback _framebufferSizeCallback;
    private bool _framebufferResized;

    private Vk _vk = null!;
    private Instance _instance;
    private SurfaceKHR _surface;
    private KhrSurface _khrSurface = null!;
    private ExtDebugUtils? _extDebugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    private PfnDebugUtilsMessengerCallbackEXT _debugCallback;
    private bool _validationLayersEnabled;
    private bool _debugUtilsEnabled;

    private PhysicalDevice _physicalDevice;
    private Device _device;
    private KhrSwapchain _khrSwapchain = null!;

    private uint _graphicsQueueFamilyIndex;
    private uint _presentQueueFamilyIndex;
    private Queue _graphicsQueue;
    private Queue _presentQueue;

    private SwapchainKHR _swapchain;
    private VkImage[] _swapchainImages = Array.Empty<VkImage>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private Framebuffer[] _swapchainFramebuffers = Array.Empty<Framebuffer>();
    private Format _swapchainImageFormat;
    private Extent2D _swapchainExtent;

    private RenderPass _renderPass;
    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = Array.Empty<CommandBuffer>();

    private readonly VkSemaphore[] _imageAvailableSemaphores;
    private readonly VkSemaphore[] _renderFinishedSemaphores;
    private readonly Fence[] _inFlightFences;
    private Fence[] _imagesInFlight = Array.Empty<Fence>();
    private int _currentFrame;

    private bool _disposed;

    private ImGuiController? _imguiController;
    private ImGuiVulkanRenderer? _imguiRenderer;
    private ImDrawDataPtr _imguiDrawData;

    // ImGui 的 draw list 会在 UpdateImGui() 中生成，并在随后 DrawFrame() 中提交到 GPU。
    // 若在同一帧 UI 构建过程中销毁纹理（例如关闭标签页时），draw list 仍可能引用该纹理，导致 Vulkan 绑定无效描述符而崩溃。
    // 因此这里做一个“至少延迟 1 帧”的销毁队列：本帧请求 -> 下一帧 DrawFrame 开始时执行真实销毁。
    private readonly List<nint> _pendingImGuiTextureDestroys = new();
    private readonly List<nint> _deferredImGuiTextureDestroys = new();
    private readonly object _imguiTextureDestroyLock = new();

    public ImGuiController? ImGuiController => _imguiController;

    public bool TryCreateImGuiTextureRgba8(ReadOnlySpan<byte> rgba8, int width, int height, out nint textureId, out string error)
    {
        textureId = nint.Zero;
        error = string.Empty;

        if (_disposed)
        {
            error = "VulkanRenderer 已释放";
            return false;
        }

        if (_imguiRenderer is null)
        {
            error = "ImGui 未启用（请先调用 EnableImGui）";
            return false;
        }

        try
        {
            textureId = _imguiRenderer.CreateTextureRgba8(rgba8, width, height);
            return textureId != nint.Zero;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            textureId = nint.Zero;
            return false;
        }
    }

    public void DestroyImGuiTexture(nint textureId)
    {
        if (_disposed)
        {
            return;
        }

        if (textureId == nint.Zero)
        {
            return;
        }

        lock (_imguiTextureDestroyLock)
        {
            _pendingImGuiTextureDestroys.Add(textureId);
        }
    }

    public VulkanRenderer(Glfw glfw, WindowHandle* window, string appName, VulkanRendererOptions? options = null)
    {
        _glfw = glfw ?? throw new ArgumentNullException(nameof(glfw));
        _window = window;
        _appName = string.IsNullOrWhiteSpace(appName) ? "WoOOLToOOLsSharp" : appName;
        _options = options ?? new VulkanRendererOptions();
        _maxFramesInFlight = Math.Clamp(_options.FramesInFlight, 2, 3);

        _imageAvailableSemaphores = new VkSemaphore[_maxFramesInFlight];
        _renderFinishedSemaphores = new VkSemaphore[_maxFramesInFlight];
        _inFlightFences = new Fence[_maxFramesInFlight];

        _framebufferSizeCallback = (_, _, _) => _framebufferResized = true;
        _glfw.SetFramebufferSizeCallback(_window, _framebufferSizeCallback);

        InitVulkan();
    }

    public void EnableImGui()
    {
        if (_disposed) return;
        if (_imguiController is not null) return;

        _imguiController = new ImGuiController();
        _imguiController.MakeCurrent();

        _imguiRenderer = new ImGuiVulkanRenderer(
            _vk,
            _physicalDevice,
            _device,
            _graphicsQueue,
            _graphicsQueueFamilyIndex,
            _renderPass,
            _swapchainImages.Length);
    }

    public void UpdateImGui(GlfwInput input, float deltaSeconds)
    {
        if (_disposed) return;
        if (_imguiController is null || _imguiRenderer is null) return;

        _glfw.GetFramebufferSize(_window, out int width, out int height);
        _imguiDrawData = _imguiController.UpdateAndRender(input, deltaSeconds, width, height);
    }

    public void DrawFrame()
    {
        if (_disposed) return;

        // 仅销毁上一帧请求的纹理，避免同帧 draw list 仍引用纹理导致崩溃。
        DrainDeferredImGuiTextureDestroys();

        Fence inFlightFence = _inFlightFences[_currentFrame];
        _vk.WaitForFences(_device, 1, &inFlightFence, true, ulong.MaxValue);

        uint imageIndex = 0;
        VkSemaphore imageAvailableSemaphore = _imageAvailableSemaphores[_currentFrame];
        Result acquireResult = _khrSwapchain.AcquireNextImage(_device, _swapchain, ulong.MaxValue, imageAvailableSemaphore, default, ref imageIndex);

        if (acquireResult == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }

        if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
        {
            throw new InvalidOperationException($"vkAcquireNextImageKHR 失败: {acquireResult}");
        }

        if (_imagesInFlight.Length > 0)
        {
            Fence previousFence = _imagesInFlight[imageIndex];
            if (previousFence.Handle != 0)
            {
                _vk.WaitForFences(_device, 1, &previousFence, true, ulong.MaxValue);
            }
            _imagesInFlight[imageIndex] = _inFlightFences[_currentFrame];
        }

        Fence resetFence = _inFlightFences[_currentFrame];
        _vk.ResetFences(_device, 1, &resetFence);

        RecordCommandBuffer(_commandBuffers[imageIndex], imageIndex);

        SubmitAndPresent(imageIndex);

        PromotePendingImGuiTextureDestroys();

        _currentFrame = (_currentFrame + 1) % _maxFramesInFlight;
    }

    private void DrainDeferredImGuiTextureDestroys()
    {
        if (_imguiRenderer is null)
        {
            lock (_imguiTextureDestroyLock)
            {
                _deferredImGuiTextureDestroys.Clear();
            }
            return;
        }

        nint[]? toDestroy = null;
        lock (_imguiTextureDestroyLock)
        {
            if (_deferredImGuiTextureDestroys.Count <= 0)
            {
                return;
            }

            toDestroy = _deferredImGuiTextureDestroys.ToArray();
            _deferredImGuiTextureDestroys.Clear();
        }

        for (int i = 0; i < toDestroy.Length; i++)
        {
            _imguiRenderer.DestroyTexture(toDestroy[i]);
        }
    }

    private void PromotePendingImGuiTextureDestroys()
    {
        lock (_imguiTextureDestroyLock)
        {
            if (_pendingImGuiTextureDestroys.Count <= 0)
            {
                return;
            }

            _deferredImGuiTextureDestroys.AddRange(_pendingImGuiTextureDestroys);
            _pendingImGuiTextureDestroys.Clear();
        }
    }

    public void WaitIdle()
    {
        if (_disposed) return;
        _vk.DeviceWaitIdle(_device);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _vk.DeviceWaitIdle(_device);

        _imguiRenderer?.Dispose();
        _imguiRenderer = null;
        _imguiController?.Dispose();
        _imguiController = null;
        _imguiDrawData = default;

        CleanupSwapchain();

        for (int i = 0; i < _maxFramesInFlight; i++)
        {
            if (_renderFinishedSemaphores[i].Handle != 0)
            {
                _vk.DestroySemaphore(_device, _renderFinishedSemaphores[i], null);
            }
            if (_imageAvailableSemaphores[i].Handle != 0)
            {
                _vk.DestroySemaphore(_device, _imageAvailableSemaphores[i], null);
            }
            if (_inFlightFences[i].Handle != 0)
            {
                _vk.DestroyFence(_device, _inFlightFences[i], null);
            }
        }

        if (_commandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _commandPool, null);
        }

        if (_device.Handle != 0)
        {
            _vk.DestroyDevice(_device, null);
        }

        if (_surface.Handle != 0)
        {
            _khrSurface.DestroySurface(_instance, _surface, null);
        }

        if (_debugMessenger.Handle != 0 && _extDebugUtils is not null)
        {
            _extDebugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
        }

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
        }

        GC.KeepAlive(_debugCallback);
        _vk.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void InitVulkan()
    {
        _vk = Vk.GetApi();

        CreateInstance();
        LoadInstanceExtensions();
        SetupDebugMessenger();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        LoadDeviceExtensions();
        CreateSwapchainObjects();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();
    }

    private void CreateInstance()
    {
        byte** requiredExtensions = _glfw.GetRequiredInstanceExtensions(out uint requiredExtensionCount);
        if (requiredExtensions is null || requiredExtensionCount == 0)
        {
            throw new InvalidOperationException("GLFW 未返回 Vulkan 所需的 Instance Extensions。");
        }

        List<string> enabledExtensions = CollectGlfwRequiredExtensions(requiredExtensions, requiredExtensionCount);

        _validationLayersEnabled = _options.EnableValidationLayers && IsInstanceLayerAvailable(ValidationLayerName);
        if (_options.EnableValidationLayers && !_validationLayersEnabled && _options.Verbose)
        {
            Console.Error.WriteLine($"未找到 {ValidationLayerName}，将跳过 Validation Layers。");
        }

        _debugUtilsEnabled = _options.EnableDebugUtils && IsInstanceExtensionAvailable(DebugUtilsExtensionName);
        if (_options.EnableDebugUtils && !_debugUtilsEnabled && _options.Verbose)
        {
            Console.Error.WriteLine($"未找到 {DebugUtilsExtensionName}，将跳过 DebugUtils/DebugMessenger。");
        }

        if (_debugUtilsEnabled && !enabledExtensions.Contains(DebugUtilsExtensionName))
        {
            enabledExtensions.Add(DebugUtilsExtensionName);
        }

        List<string> enabledLayers = _validationLayersEnabled
            ? new List<string> { ValidationLayerName }
            : new List<string>();

        nint appNamePtr = SilkMarshal.StringToPtr(_appName, NativeStringEncoding.UTF8);
        nint engineNamePtr = SilkMarshal.StringToPtr("WoOOLToOOLsSharp", NativeStringEncoding.UTF8);
        nint enabledExtensionsPtr = SilkMarshal.StringArrayToPtr(enabledExtensions, NativeStringEncoding.UTF8);
        nint enabledLayersPtr = enabledLayers.Count > 0
            ? SilkMarshal.StringArrayToPtr(enabledLayers, NativeStringEncoding.UTF8)
            : 0;

        try
        {
            ApplicationInfo appInfo = new()
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)appNamePtr,
                ApplicationVersion = Vk.MakeVersion(0, 1, 0),
                PEngineName = (byte*)engineNamePtr,
                EngineVersion = Vk.MakeVersion(0, 1, 0),
                ApiVersion = Vk.Version11
            };

            InstanceCreateInfo createInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)enabledExtensions.Count,
                PpEnabledExtensionNames = (byte**)enabledExtensionsPtr,
                EnabledLayerCount = (uint)enabledLayers.Count,
                PpEnabledLayerNames = enabledLayers.Count > 0 ? (byte**)enabledLayersPtr : null
            };

            Result result = _vk.CreateInstance(&createInfo, null, out _instance);
            Ensure(result, "vkCreateInstance");
        }
        finally
        {
            SilkMarshal.Free(appNamePtr);
            SilkMarshal.Free(engineNamePtr);
            SilkMarshal.Free(enabledExtensionsPtr);
            if (enabledLayersPtr != 0)
            {
                SilkMarshal.Free(enabledLayersPtr);
            }
        }
    }

    private void CreateSurface()
    {
        VkNonDispatchableHandle surfaceHandle = default;
        VkHandle instanceHandle = new(_instance.Handle);

        int rawResult = _glfw.CreateWindowSurface(instanceHandle, _window, null, &surfaceHandle);
        Result result = (Result)rawResult;
        Ensure(result, "glfwCreateWindowSurface");

        _surface = new SurfaceKHR(surfaceHandle.Handle);
    }

    private void LoadInstanceExtensions()
    {
        if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
        {
            throw new InvalidOperationException("加载 Vulkan Instance 扩展 KhrSurface 失败。");
        }

        if (_debugUtilsEnabled)
        {
            if (_vk.TryGetInstanceExtension(_instance, out ExtDebugUtils extDebugUtils))
            {
                _extDebugUtils = extDebugUtils;
            }
            else
            {
                _debugUtilsEnabled = false;
                if (_options.Verbose)
                {
                    Console.Error.WriteLine("DebugUtils 已启用但加载 ExtDebugUtils 失败，将跳过 DebugMessenger/ObjectName。");
                }
            }
        }
    }

    private void SetupDebugMessenger()
    {
        if (!_debugUtilsEnabled || _extDebugUtils is null)
        {
            return;
        }

        _debugCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback);

        DebugUtilsMessengerCreateInfoEXT createInfo = new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                          | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                          | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = _debugCallback,
            PUserData = null
        };

#if DEBUG
        createInfo.MessageSeverity |= DebugUtilsMessageSeverityFlagsEXT.InfoBitExt;
#endif

        Result result = _extDebugUtils.CreateDebugUtilsMessenger(_instance, &createInfo, null, out _debugMessenger);
        if (result != Result.Success && _options.Verbose)
        {
            Console.Error.WriteLine($"创建 DebugUtilsMessenger 失败: {result}");
        }
    }

    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData)
    {
        _ = pUserData;

        string message = pCallbackData is null
            ? string.Empty
            : (SilkMarshal.PtrToString((nint)pCallbackData->PMessage, NativeStringEncoding.UTF8) ?? string.Empty);

        Console.Error.WriteLine($"[Vulkan] {messageSeverity} {messageTypes}: {message}");
        return 0;
    }

    private void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, null);
        if (deviceCount == 0)
        {
            throw new InvalidOperationException("未找到支持 Vulkan 的物理设备。");
        }

        PhysicalDevice* devices = stackalloc PhysicalDevice[(int)deviceCount];
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devices);

        string? preferred = string.IsNullOrWhiteSpace(_options.PreferredDeviceName) ? null : _options.PreferredDeviceName.Trim();

        bool foundAny = false;
        PhysicalDevice bestOverallDevice = default;
        PhysicalDeviceProperties bestOverallProps = default;
        string bestOverallName = string.Empty;
        int bestOverallScore = int.MinValue;

        bool foundPreferred = false;
        PhysicalDevice bestPreferredDevice = default;
        PhysicalDeviceProperties bestPreferredProps = default;
        string bestPreferredName = string.Empty;
        int bestPreferredScore = int.MinValue;

        var verboseCandidates = _options.Verbose ? new List<string>() : null;

        for (int i = 0; i < deviceCount; i++)
        {
            PhysicalDevice device = devices[i];
            if (!IsDeviceSuitable(device))
            {
                continue;
            }

            _vk.GetPhysicalDeviceProperties(device, out PhysicalDeviceProperties props);
            string name = GetPhysicalDeviceName(props);
            int score = ScorePhysicalDevice(props, name);

            foundAny = true;

            if (verboseCandidates is not null)
            {
                verboseCandidates.Add($"{name} ({props.DeviceType}) score={score}");
            }

            if (score > bestOverallScore)
            {
                bestOverallScore = score;
                bestOverallDevice = device;
                bestOverallProps = props;
                bestOverallName = name;
            }

            if (preferred is not null && name.Contains(preferred, StringComparison.OrdinalIgnoreCase))
            {
                if (!foundPreferred || score > bestPreferredScore)
                {
                    foundPreferred = true;
                    bestPreferredScore = score;
                    bestPreferredDevice = device;
                    bestPreferredProps = props;
                    bestPreferredName = name;
                }
            }
        }

        if (!foundAny)
        {
            throw new InvalidOperationException("未找到满足 Swapchain/队列要求的物理设备。");
        }

        if (preferred is not null && foundPreferred)
        {
            _physicalDevice = bestPreferredDevice;
            if (_options.Verbose)
            {
                Console.WriteLine("[Vulkan] 物理设备候选：");
                foreach (string line in verboseCandidates ?? (IEnumerable<string>)Array.Empty<string>())
                {
                    Console.WriteLine($"  - {line}");
                }
                Console.WriteLine($"[Vulkan] 已按名称优先选择: {bestPreferredName} ({bestPreferredProps.DeviceType})");
            }
            return;
        }

        _physicalDevice = bestOverallDevice;
        if (_options.Verbose)
        {
            Console.WriteLine("[Vulkan] 物理设备候选：");
            foreach (string line in verboseCandidates ?? (IEnumerable<string>)Array.Empty<string>())
            {
                Console.WriteLine($"  - {line}");
            }
            if (preferred is not null && !foundPreferred)
            {
                Console.Error.WriteLine($"[Vulkan] 未找到名称匹配的 GPU: {preferred}，已回退到评分最高的设备。");
            }
            Console.WriteLine($"[Vulkan] 已选择: {bestOverallName} ({bestOverallProps.DeviceType})");
        }
    }

    private static string GetPhysicalDeviceName(in PhysicalDeviceProperties props)
    {
        fixed (byte* namePtr = props.DeviceName)
        {
            return SilkMarshal.PtrToString((nint)namePtr, NativeStringEncoding.UTF8) ?? "Unknown";
        }
    }

    private int ScorePhysicalDevice(in PhysicalDeviceProperties props, string name)
    {
        int score = 0;

        score += _options.PreferDiscreteGpu
            ? props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 1000,
                PhysicalDeviceType.IntegratedGpu => 100,
                PhysicalDeviceType.VirtualGpu => 50,
                PhysicalDeviceType.Cpu => 10,
                _ => 10
            }
            : props.DeviceType switch
            {
                PhysicalDeviceType.IntegratedGpu => 1000,
                PhysicalDeviceType.DiscreteGpu => 900,
                PhysicalDeviceType.VirtualGpu => 50,
                PhysicalDeviceType.Cpu => 10,
                _ => 10
            };

        score += (int)(props.Limits.MaxImageDimension2D / 1024);

        if (!string.IsNullOrWhiteSpace(_options.PreferredDeviceName) &&
            name.Contains(_options.PreferredDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 10000;
        }

        return score;
    }

    private bool IsDeviceSuitable(PhysicalDevice device)
    {
        QueueFamilyIndices indices = FindQueueFamilies(device);
        if (!indices.IsComplete) return false;

        if (!CheckDeviceExtensionSupport(device)) return false;

        SwapchainSupportDetails swapchainSupport = QuerySwapchainSupport(device);
        return swapchainSupport.Formats.Count > 0 && swapchainSupport.PresentModes.Count > 0;
    }

    private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
    {
        uint queueFamilyCount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, null);

        QueueFamilyProperties* queueFamilies = stackalloc QueueFamilyProperties[(int)queueFamilyCount];
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, queueFamilies);

        uint? graphicsIndex = null;
        uint? presentIndex = null;

        for (uint i = 0; i < queueFamilyCount; i++)
        {
            QueueFamilyProperties props = queueFamilies[i];

            if ((props.QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                graphicsIndex ??= i;
            }

            Bool32 presentSupport = default;
            _khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, &presentSupport);
            if (presentSupport.Value != 0)
            {
                presentIndex ??= i;
            }

            if (graphicsIndex.HasValue && presentIndex.HasValue)
            {
                break;
            }
        }

        return new QueueFamilyIndices(graphicsIndex, presentIndex);
    }

    private bool CheckDeviceExtensionSupport(PhysicalDevice device)
    {
        uint extensionCount = 0;
        _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, null);

        ExtensionProperties* extensions = stackalloc ExtensionProperties[(int)extensionCount];
        _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, extensions);

        var available = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < extensionCount; i++)
        {
            byte* namePtr = extensions[i].ExtensionName;
            string? name = SilkMarshal.PtrToString((nint)namePtr, NativeStringEncoding.UTF8);
            if (!string.IsNullOrEmpty(name))
            {
                available.Add(name);
            }
        }

        return available.Contains(KhrSwapchain.ExtensionName);
    }

    private SwapchainSupportDetails QuerySwapchainSupport(PhysicalDevice device)
    {
        SurfaceCapabilitiesKHR capabilities;
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(device, _surface, &capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, &formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        if (formatCount > 0)
        {
            fixed (SurfaceFormatKHR* formatsPtr = formats)
            {
                _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, &formatCount, formatsPtr);
            }
        }

        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, &presentModeCount, null);
        var presentModes = new PresentModeKHR[presentModeCount];
        if (presentModeCount > 0)
        {
            fixed (PresentModeKHR* presentModesPtr = presentModes)
            {
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, &presentModeCount, presentModesPtr);
            }
        }

        return new SwapchainSupportDetails(capabilities, formats, presentModes);
    }

    private void CreateLogicalDevice()
    {
        QueueFamilyIndices indices = FindQueueFamilies(_physicalDevice);
        if (!indices.IsComplete)
        {
            throw new InvalidOperationException("选择到的物理设备没有可用的 Graphics/Present 队列。");
        }

        _graphicsQueueFamilyIndex = indices.GraphicsFamily!.Value;
        _presentQueueFamilyIndex = indices.PresentFamily!.Value;

        float queuePriority = 1.0f;

        var uniqueQueueFamilies = _graphicsQueueFamilyIndex == _presentQueueFamilyIndex
            ? new[] { _graphicsQueueFamilyIndex }
            : new[] { _graphicsQueueFamilyIndex, _presentQueueFamilyIndex };

        var queueCreateInfos = new DeviceQueueCreateInfo[uniqueQueueFamilies.Length];
        for (int i = 0; i < uniqueQueueFamilies.Length; i++)
        {
            queueCreateInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueQueueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &queuePriority
            };
        }

        var requiredDeviceExtensions = new List<string> { KhrSwapchain.ExtensionName };
        nint enabledExtensionNamesPtr = SilkMarshal.StringArrayToPtr(requiredDeviceExtensions, NativeStringEncoding.UTF8);

        try
        {
            PhysicalDeviceFeatures deviceFeatures = default;

            fixed (DeviceQueueCreateInfo* queueCreateInfosPtr = queueCreateInfos)
            {
                DeviceCreateInfo createInfo = new()
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = (uint)queueCreateInfos.Length,
                    PQueueCreateInfos = queueCreateInfosPtr,
                    EnabledExtensionCount = (uint)requiredDeviceExtensions.Count,
                    PpEnabledExtensionNames = (byte**)enabledExtensionNamesPtr,
                    PEnabledFeatures = &deviceFeatures
                };

                Result result = _vk.CreateDevice(_physicalDevice, &createInfo, null, out _device);
                Ensure(result, "vkCreateDevice");
            }
        }
        finally
        {
            SilkMarshal.Free(enabledExtensionNamesPtr);
        }

        _vk.GetDeviceQueue(_device, _graphicsQueueFamilyIndex, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentQueueFamilyIndex, 0, out _presentQueue);

        TrySetObjectName(ObjectType.Device, unchecked((ulong)_device.Handle), "Device");
        TrySetObjectName(ObjectType.Queue, unchecked((ulong)_graphicsQueue.Handle), "GraphicsQueue");
        TrySetObjectName(ObjectType.Queue, unchecked((ulong)_presentQueue.Handle), "PresentQueue");
    }

    private void LoadDeviceExtensions()
    {
        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
        {
            throw new InvalidOperationException("加载 Vulkan Device 扩展 KhrSwapchain 失败。");
        }
    }

    private void CreateSwapchainObjects()
    {
        CreateSwapchain();
        CreateImageViews();
        CreateRenderPass();
        CreateFramebuffers();
        _imagesInFlight = new Fence[_swapchainImages.Length];
        _imguiRenderer?.OnSwapchainRecreated(_renderPass, _swapchainImages.Length);
    }

    private void CreateSwapchain()
    {
        SwapchainSupportDetails swapchainSupport = QuerySwapchainSupport(_physicalDevice);
        SurfaceFormatKHR surfaceFormat = ChooseSwapSurfaceFormat(swapchainSupport.Formats);
        PresentModeKHR presentMode = ChooseSwapPresentMode(swapchainSupport.PresentModes);
        Extent2D extent = ChooseSwapExtent(swapchainSupport.Capabilities);

        uint imageCount = swapchainSupport.Capabilities.MinImageCount + 1;
        if (swapchainSupport.Capabilities.MaxImageCount > 0 && imageCount > swapchainSupport.Capabilities.MaxImageCount)
        {
            imageCount = swapchainSupport.Capabilities.MaxImageCount;
        }

        uint* queueFamilyIndices = stackalloc uint[2] { _graphicsQueueFamilyIndex, _presentQueueFamilyIndex };

        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            PreTransform = swapchainSupport.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default
        };

        if (_graphicsQueueFamilyIndex != _presentQueueFamilyIndex)
        {
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = queueFamilyIndices;
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
            createInfo.QueueFamilyIndexCount = 0;
            createInfo.PQueueFamilyIndices = null;
        }

        Result result = _khrSwapchain.CreateSwapchain(_device, &createInfo, null, out _swapchain);
        Ensure(result, "vkCreateSwapchainKHR");

        uint swapchainImageCount = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, null);
        _swapchainImages = new VkImage[swapchainImageCount];
        fixed (VkImage* swapchainImagesPtr = _swapchainImages)
        {
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, swapchainImagesPtr);
        }

        _swapchainImageFormat = surfaceFormat.Format;
        _swapchainExtent = extent;

        TrySetObjectName(ObjectType.SwapchainKhr, _swapchain.Handle, "Swapchain");
        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            TrySetObjectName(ObjectType.Image, _swapchainImages[i].Handle, $"SwapchainImage[{i}]");
        }
    }

    private void CreateImageViews()
    {
        _swapchainImageViews = new ImageView[_swapchainImages.Length];

        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainImageFormat,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            Result result = _vk.CreateImageView(_device, &createInfo, null, out _swapchainImageViews[i]);
            Ensure(result, "vkCreateImageView");
            TrySetObjectName(ObjectType.ImageView, _swapchainImageViews[i].Handle, $"SwapchainImageView[{i}]");
        }
    }

    private void CreateRenderPass()
    {
        AttachmentDescription colorAttachment = new()
        {
            Format = _swapchainImageFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        AttachmentReference colorAttachmentRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };

        RenderPassCreateInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };

        Result result = _vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass);
        Ensure(result, "vkCreateRenderPass");
        TrySetObjectName(ObjectType.RenderPass, _renderPass.Handle, "RenderPass");
    }

    private void CreateFramebuffers()
    {
        _swapchainFramebuffers = new Framebuffer[_swapchainImageViews.Length];

        for (int i = 0; i < _swapchainImageViews.Length; i++)
        {
            ImageView attachment = _swapchainImageViews[i];

            FramebufferCreateInfo framebufferInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                Layers = 1
            };

            Result result = _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _swapchainFramebuffers[i]);
            Ensure(result, "vkCreateFramebuffer");
            TrySetObjectName(ObjectType.Framebuffer, _swapchainFramebuffers[i].Handle, $"Framebuffer[{i}]");
        }
    }

    private void CreateCommandPool()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsQueueFamilyIndex
        };

        Result result = _vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool);
        Ensure(result, "vkCreateCommandPool");
        TrySetObjectName(ObjectType.CommandPool, _commandPool.Handle, "CommandPool");
    }

    private void CreateCommandBuffers()
    {
        if (_commandBuffers.Length > 0)
        {
            fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
            {
                _vk.FreeCommandBuffers(_device, _commandPool, (uint)_commandBuffers.Length, commandBuffersPtr);
            }
        }

        _commandBuffers = new CommandBuffer[_swapchainFramebuffers.Length];

        fixed (CommandBuffer* commandBuffersPtr = _commandBuffers)
        {
            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)_commandBuffers.Length
            };

            Result result = _vk.AllocateCommandBuffers(_device, &allocInfo, commandBuffersPtr);
            Ensure(result, "vkAllocateCommandBuffers");
        }

        for (int i = 0; i < _commandBuffers.Length; i++)
        {
            TrySetObjectName(ObjectType.CommandBuffer, unchecked((ulong)_commandBuffers[i].Handle), $"CommandBuffer[{i}]");
        }
    }

    private void RecordCommandBuffer(CommandBuffer commandBuffer, uint imageIndex)
    {
        _vk.ResetCommandBuffer(commandBuffer, 0);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo
        };

        Result result = _vk.BeginCommandBuffer(commandBuffer, &beginInfo);
        Ensure(result, "vkBeginCommandBuffer");

        ClearValue clearValue = default;
        clearValue.Color = new ClearColorValue
        {
            Float32_0 = 0.08f,
            Float32_1 = 0.10f,
            Float32_2 = 0.16f,
            Float32_3 = 1.0f
        };

        RenderPassBeginInfo renderPassInfo = new()
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _swapchainFramebuffers[imageIndex],
            RenderArea = new Rect2D
            {
                Offset = new Offset2D(0, 0),
                Extent = _swapchainExtent
            },
            ClearValueCount = 1,
            PClearValues = &clearValue
        };

        _vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);
        _imguiRenderer?.Render(_imguiDrawData, commandBuffer, (int)_swapchainExtent.Width, (int)_swapchainExtent.Height, unchecked((int)imageIndex));
        _vk.CmdEndRenderPass(commandBuffer);

        result = _vk.EndCommandBuffer(commandBuffer);
        Ensure(result, "vkEndCommandBuffer");
    }

    private void SubmitAndPresent(uint imageIndex)
    {
        VkSemaphore waitSemaphore = _imageAvailableSemaphores[_currentFrame];
        VkSemaphore signalSemaphore = _renderFinishedSemaphores[_currentFrame];
        CommandBuffer commandBuffer = _commandBuffers[imageIndex];
        Fence inFlightFence = _inFlightFences[_currentFrame];

        VkSemaphore* waitSemaphoresPtr = stackalloc VkSemaphore[1];
        waitSemaphoresPtr[0] = waitSemaphore;

        VkSemaphore* signalSemaphoresPtr = stackalloc VkSemaphore[1];
        signalSemaphoresPtr[0] = signalSemaphore;

        PipelineStageFlags* waitStagesPtr = stackalloc PipelineStageFlags[1];
        waitStagesPtr[0] = PipelineStageFlags.ColorAttachmentOutputBit;

        CommandBuffer* commandBuffersPtr = stackalloc CommandBuffer[1];
        commandBuffersPtr[0] = commandBuffer;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = waitSemaphoresPtr,
            PWaitDstStageMask = waitStagesPtr,
            CommandBufferCount = 1,
            PCommandBuffers = commandBuffersPtr,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = signalSemaphoresPtr
        };

        Result result = _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, inFlightFence);
        Ensure(result, "vkQueueSubmit");

        SwapchainKHR* swapchainsPtr = stackalloc SwapchainKHR[1];
        swapchainsPtr[0] = _swapchain;

        uint* imageIndicesPtr = stackalloc uint[1];
        imageIndicesPtr[0] = imageIndex;

        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = signalSemaphoresPtr,
            SwapchainCount = 1,
            PSwapchains = swapchainsPtr,
            PImageIndices = imageIndicesPtr
        };

        result = _khrSwapchain.QueuePresent(_presentQueue, &presentInfo);
        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || _framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapchain();
            return;
        }

        Ensure(result, "vkQueuePresentKHR");
    }

    private void RecreateSwapchain()
    {
        int width = 0;
        int height = 0;
        while (width == 0 || height == 0)
        {
            _glfw.GetFramebufferSize(_window, out width, out height);
            _glfw.WaitEvents();
        }

        _vk.DeviceWaitIdle(_device);

        CleanupSwapchain();
        CreateSwapchainObjects();
        CreateCommandBuffers();
    }

    private void CleanupSwapchain()
    {
        _imguiRenderer?.DisposeSwapchainResources();

        if (_swapchainFramebuffers.Length > 0)
        {
            for (int i = 0; i < _swapchainFramebuffers.Length; i++)
            {
                if (_swapchainFramebuffers[i].Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, _swapchainFramebuffers[i], null);
                }
            }
        }

        if (_renderPass.Handle != 0)
        {
            _vk.DestroyRenderPass(_device, _renderPass, null);
            _renderPass = default;
        }

        if (_swapchainImageViews.Length > 0)
        {
            for (int i = 0; i < _swapchainImageViews.Length; i++)
            {
                if (_swapchainImageViews[i].Handle != 0)
                {
                    _vk.DestroyImageView(_device, _swapchainImageViews[i], null);
                }
            }
        }

        if (_swapchain.Handle != 0)
        {
            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
            _swapchain = default;
        }

        _swapchainFramebuffers = Array.Empty<Framebuffer>();
        _swapchainImageViews = Array.Empty<ImageView>();
        _swapchainImages = Array.Empty<VkImage>();
    }

    private SurfaceFormatKHR ChooseSwapSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> availableFormats)
    {
        for (int i = 0; i < availableFormats.Count; i++)
        {
            if (availableFormats[i].Format == Format.B8G8R8A8Srgb &&
                availableFormats[i].ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return availableFormats[i];
            }
        }

        return availableFormats.Count > 0 ? availableFormats[0] : new SurfaceFormatKHR { Format = Format.B8G8R8A8Unorm };
    }

    private PresentModeKHR ChooseSwapPresentMode(IReadOnlyList<PresentModeKHR> availablePresentModes)
    {
        for (int i = 0; i < availablePresentModes.Count; i++)
        {
            if (availablePresentModes[i] == PresentModeKHR.MailboxKhr)
            {
                return availablePresentModes[i];
            }
        }

        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        _glfw.GetFramebufferSize(_window, out int width, out int height);

        Extent2D actual = new((uint)Math.Clamp(width, (int)capabilities.MinImageExtent.Width, (int)capabilities.MaxImageExtent.Width),
            (uint)Math.Clamp(height, (int)capabilities.MinImageExtent.Height, (int)capabilities.MaxImageExtent.Height));

        return actual;
    }

    private void CreateSyncObjects()
    {
        SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        for (int i = 0; i < _maxFramesInFlight; i++)
        {
            Result result = _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailableSemaphores[i]);
            Ensure(result, "vkCreateSemaphore(imageAvailable)");
            TrySetObjectName(ObjectType.Semaphore, _imageAvailableSemaphores[i].Handle, $"ImageAvailableSemaphore[{i}]");

            result = _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinishedSemaphores[i]);
            Ensure(result, "vkCreateSemaphore(renderFinished)");
            TrySetObjectName(ObjectType.Semaphore, _renderFinishedSemaphores[i].Handle, $"RenderFinishedSemaphore[{i}]");

            result = _vk.CreateFence(_device, &fenceInfo, null, out _inFlightFences[i]);
            Ensure(result, "vkCreateFence(inFlight)");
            TrySetObjectName(ObjectType.Fence, _inFlightFences[i].Handle, $"InFlightFence[{i}]");
        }
    }

    private void TrySetObjectName(ObjectType objectType, ulong handle, string name)
    {
        if (!_debugUtilsEnabled || _extDebugUtils is null)
        {
            return;
        }

        if (_device.Handle == 0 || handle == 0 || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        nint namePtr = SilkMarshal.StringToPtr(name, NativeStringEncoding.UTF8);
        try
        {
            DebugUtilsObjectNameInfoEXT info = new()
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = objectType,
                ObjectHandle = handle,
                PObjectName = (byte*)namePtr
            };

            Result result = _extDebugUtils.SetDebugUtilsObjectName(_device, &info);
            if (result != Result.Success && _options.Verbose)
            {
                Console.Error.WriteLine($"设置 Vulkan 对象名称失败: {objectType} {name} ({result})");
            }
        }
        finally
        {
            SilkMarshal.Free(namePtr);
        }
    }

    private static List<string> CollectGlfwRequiredExtensions(byte** requiredExtensions, uint requiredExtensionCount)
    {
        var extensions = new List<string>((int)requiredExtensionCount);
        for (int i = 0; i < requiredExtensionCount; i++)
        {
            string? ext = SilkMarshal.PtrToString((nint)requiredExtensions[i], NativeStringEncoding.UTF8);
            if (!string.IsNullOrWhiteSpace(ext) && !extensions.Contains(ext))
            {
                extensions.Add(ext);
            }
        }

        return extensions;
    }

    private bool IsInstanceExtensionAvailable(string extensionName)
    {
        uint extensionCount = 0;
        _vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null);
        if (extensionCount == 0)
        {
            return false;
        }

        ExtensionProperties* extensions = stackalloc ExtensionProperties[(int)extensionCount];
        _vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, extensions);

        for (int i = 0; i < extensionCount; i++)
        {
            byte* namePtr = extensions[i].ExtensionName;
            string? name = SilkMarshal.PtrToString((nint)namePtr, NativeStringEncoding.UTF8);
            if (string.Equals(name, extensionName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsInstanceLayerAvailable(string layerName)
    {
        uint layerCount = 0;
        _vk.EnumerateInstanceLayerProperties(&layerCount, null);
        if (layerCount == 0)
        {
            return false;
        }

        LayerProperties* layers = stackalloc LayerProperties[(int)layerCount];
        _vk.EnumerateInstanceLayerProperties(&layerCount, layers);

        for (int i = 0; i < layerCount; i++)
        {
            byte* namePtr = layers[i].LayerName;
            string? name = SilkMarshal.PtrToString((nint)namePtr, NativeStringEncoding.UTF8);
            if (string.Equals(name, layerName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void Ensure(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} 失败: {result}");
        }
    }

    private readonly record struct QueueFamilyIndices(uint? GraphicsFamily, uint? PresentFamily)
    {
        public bool IsComplete => GraphicsFamily.HasValue && PresentFamily.HasValue;
    }

    private readonly record struct SwapchainSupportDetails(
        SurfaceCapabilitiesKHR Capabilities,
        IReadOnlyList<SurfaceFormatKHR> Formats,
        IReadOnlyList<PresentModeKHR> PresentModes);
}

