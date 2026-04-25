using System;
using System.Diagnostics;
using Silk.NET.GLFW;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

public static unsafe class VulkanAppRunner
{
    private enum AutoWindowActionKind
    {
        Resize,
        Iconify,
        Restore,
        Close,
    }

    private readonly record struct AutoWindowAction(int Frame, AutoWindowActionKind Kind, int Width, int Height);

    public static int Run(string title, int width = 1280, int height = 720)
    {
        return RunInternal(title, app: null, width, height);
    }

    public static int Run(string title, IVulkanApp app, int width = 1280, int height = 720)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        return RunInternal(title, app, width, height);
    }

    private static int RunInternal(string title, IVulkanApp? app, int width, int height)
    {
        Glfw glfw = Glfw.GetApi();

        GlfwCallbacks.ErrorCallback errorCallback = OnGlfwError;
        glfw.SetErrorCallback(errorCallback);

        if (!glfw.Init())
        {
            Console.Error.WriteLine("GLFW 初始化失败。");
            return -1;
        }

        if (!glfw.VulkanSupported())
        {
            Console.Error.WriteLine("当前环境不支持 Vulkan（或 Vulkan Loader 不可用）。");
            glfw.Terminate();
            return -1;
        }

        glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        glfw.WindowHint(WindowHintBool.Resizable, true);

        WindowHandle* window = glfw.CreateWindow(width, height, title, null, null);
        if (window is null)
        {
            Console.Error.WriteLine("GLFW 创建窗口失败。");
            glfw.Terminate();
            return -1;
        }

        VulkanRendererOptions options = VulkanRendererOptions.FromEnvironment();

        using GlfwInput input = new(glfw, window);
        using VulkanRenderer renderer = new(glfw, window, title, options);
        renderer.EnableImGui();

        if (app is not null && renderer.ImGuiController is not null)
        {
            try
            {
                app.ConfigureImGui(renderer, renderer.ImGuiController);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"应用 ConfigureImGui 失败: {ex}");
            }
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        double lastTime = stopwatch.Elapsed.TotalSeconds;
        long startMilliseconds = stopwatch.ElapsedMilliseconds;

        int autoExitMs = ReadIntEnvironment("WOOOL_AUTO_EXIT_MS", 0);
        int autoExitFrames = ReadIntEnvironment("WOOOL_AUTO_EXIT_FRAMES", 0);
        bool autoExitTriggered = false;
        int frameCount = 0;

        AutoWindowAction[] autoWindowActions = BuildAutoWindowActions();
        int nextAutoWindowActionIndex = 0;

        while (!glfw.WindowShouldClose(window))
        {
            double now = stopwatch.Elapsed.TotalSeconds;
            float deltaSeconds = (float)(now - lastTime);
            lastTime = now;

            input.BeginFrame();
            glfw.PollEvents();

            if (autoWindowActions.Length > 0)
            {
                while (nextAutoWindowActionIndex < autoWindowActions.Length
                       && frameCount >= autoWindowActions[nextAutoWindowActionIndex].Frame)
                {
                    ExecuteAutoWindowAction(glfw, window, autoWindowActions[nextAutoWindowActionIndex]);
                    nextAutoWindowActionIndex++;
                }
            }

            if (app is not null)
            {
                try
                {
                    app.Tick(input, deltaSeconds);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"应用 Tick 发生异常，将触发退出: {ex}");
                    glfw.SetWindowShouldClose(window, true);
                }

                if (app.RequestExit)
                {
                    glfw.SetWindowShouldClose(window, true);
                }
            }

            renderer.UpdateImGui(input, deltaSeconds);
            renderer.DrawFrame();

            frameCount++;

            if (!autoExitTriggered)
            {
                long elapsedMs = stopwatch.ElapsedMilliseconds - startMilliseconds;
                if (autoExitFrames > 0 && frameCount >= autoExitFrames)
                {
                    autoExitTriggered = true;
                    Console.Error.WriteLine($"自动退出触发：已渲染 {frameCount} 帧。");
                    glfw.SetWindowShouldClose(window, true);
                }
                else if (autoExitMs > 0 && elapsedMs >= autoExitMs)
                {
                    autoExitTriggered = true;
                    Console.Error.WriteLine($"自动退出触发：已运行 {elapsedMs} ms（{frameCount} 帧）。");
                    glfw.SetWindowShouldClose(window, true);
                }
            }
        }

        renderer.WaitIdle();

        try
        {
            app?.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"应用 Dispose 发生异常: {ex}");
        }

        glfw.DestroyWindow(window);
        glfw.Terminate();

        GC.KeepAlive(input);
        GC.KeepAlive(errorCallback);
        return 0;
    }

    private static void OnGlfwError(ErrorCode error, string description)
    {
        Console.Error.WriteLine($"GLFW 错误 {error}: {description}");
    }

    private static int ReadIntEnvironment(string name, int defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int parsed) ? parsed : defaultValue;
    }

    private static bool ReadBoolEnvironment(string name, bool defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        string v = value.Trim();
        if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }

    private static AutoWindowAction[] BuildAutoWindowActions()
    {
        if (!ReadBoolEnvironment("WOOOL_AUTO_WINDOW_SMOKE", defaultValue: false))
        {
            return Array.Empty<AutoWindowAction>();
        }

        (int w1, int h1) = ReadSizeEnvironment("WOOOL_AUTO_WINDOW_SMOKE_RESIZE1", fallbackWidth: 800, fallbackHeight: 600);
        (int w2, int h2) = ReadSizeEnvironment("WOOOL_AUTO_WINDOW_SMOKE_RESIZE2", fallbackWidth: 1280, fallbackHeight: 720);

        return
        [
            new AutoWindowAction(Frame: 5, Kind: AutoWindowActionKind.Resize, Width: w1, Height: h1),
            new AutoWindowAction(Frame: 10, Kind: AutoWindowActionKind.Resize, Width: w2, Height: h2),
            new AutoWindowAction(Frame: 15, Kind: AutoWindowActionKind.Iconify, Width: 0, Height: 0),
            new AutoWindowAction(Frame: 20, Kind: AutoWindowActionKind.Restore, Width: 0, Height: 0),
            new AutoWindowAction(Frame: 25, Kind: AutoWindowActionKind.Close, Width: 0, Height: 0),
        ];
    }

    private static (int Width, int Height) ReadSizeEnvironment(string name, int fallbackWidth, int fallbackHeight)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return (fallbackWidth, fallbackHeight);
        }

        string v = value.Trim();
        int sep = v.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (sep <= 0 || sep >= v.Length - 1)
        {
            return (fallbackWidth, fallbackHeight);
        }

        if (!int.TryParse(v[..sep], out int w) || !int.TryParse(v[(sep + 1)..], out int h))
        {
            return (fallbackWidth, fallbackHeight);
        }

        w = Math.Clamp(w, 64, 16_384);
        h = Math.Clamp(h, 64, 16_384);
        return (w, h);
    }

    private static void ExecuteAutoWindowAction(Glfw glfw, WindowHandle* window, AutoWindowAction action)
    {
        try
        {
            switch (action.Kind)
            {
                case AutoWindowActionKind.Resize:
                    Console.Error.WriteLine($"自动窗口动作：Resize {action.Width}x{action.Height}（frame={action.Frame}）");
                    glfw.SetWindowSize(window, action.Width, action.Height);
                    break;
                case AutoWindowActionKind.Iconify:
                    Console.Error.WriteLine($"自动窗口动作：Iconify（frame={action.Frame}）");
                    glfw.IconifyWindow(window);
                    break;
                case AutoWindowActionKind.Restore:
                    Console.Error.WriteLine($"自动窗口动作：Restore（frame={action.Frame}）");
                    glfw.RestoreWindow(window);
                    break;
                case AutoWindowActionKind.Close:
                    Console.Error.WriteLine($"自动窗口动作：Close（frame={action.Frame}）");
                    glfw.SetWindowShouldClose(window, true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"自动窗口动作失败（{action.Kind}）：{ex.Message}");
        }
    }
}
