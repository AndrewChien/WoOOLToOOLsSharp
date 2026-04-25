using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using ImGuiNET;
using Silk.NET.GLFW;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

public sealed class ImGuiController : IDisposable
{
    private static readonly IReadOnlyDictionary<Keys, ImGuiKey> KeyMap = new Dictionary<Keys, ImGuiKey>
    {
        { Keys.Tab, ImGuiKey.Tab },
        { Keys.Left, ImGuiKey.LeftArrow },
        { Keys.Right, ImGuiKey.RightArrow },
        { Keys.Up, ImGuiKey.UpArrow },
        { Keys.Down, ImGuiKey.DownArrow },
        { Keys.PageUp, ImGuiKey.PageUp },
        { Keys.PageDown, ImGuiKey.PageDown },
        { Keys.Home, ImGuiKey.Home },
        { Keys.End, ImGuiKey.End },
        { Keys.Insert, ImGuiKey.Insert },
        { Keys.Delete, ImGuiKey.Delete },
        { Keys.Backspace, ImGuiKey.Backspace },
        { Keys.Space, ImGuiKey.Space },
        { Keys.Enter, ImGuiKey.Enter },
        { Keys.Escape, ImGuiKey.Escape },

        { Keys.A, ImGuiKey.A },
        { Keys.B, ImGuiKey.B },
        { Keys.C, ImGuiKey.C },
        { Keys.D, ImGuiKey.D },
        { Keys.E, ImGuiKey.E },
        { Keys.F, ImGuiKey.F },
        { Keys.G, ImGuiKey.G },
        { Keys.H, ImGuiKey.H },
        { Keys.I, ImGuiKey.I },
        { Keys.J, ImGuiKey.J },
        { Keys.K, ImGuiKey.K },
        { Keys.L, ImGuiKey.L },
        { Keys.M, ImGuiKey.M },
        { Keys.N, ImGuiKey.N },
        { Keys.O, ImGuiKey.O },
        { Keys.P, ImGuiKey.P },
        { Keys.Q, ImGuiKey.Q },
        { Keys.R, ImGuiKey.R },
        { Keys.S, ImGuiKey.S },
        { Keys.T, ImGuiKey.T },
        { Keys.U, ImGuiKey.U },
        { Keys.V, ImGuiKey.V },
        { Keys.W, ImGuiKey.W },
        { Keys.X, ImGuiKey.X },
        { Keys.Y, ImGuiKey.Y },
        { Keys.Z, ImGuiKey.Z },

        { Keys.Number0, ImGuiKey._0 },
        { Keys.Number1, ImGuiKey._1 },
        { Keys.Number2, ImGuiKey._2 },
        { Keys.Number3, ImGuiKey._3 },
        { Keys.Number4, ImGuiKey._4 },
        { Keys.Number5, ImGuiKey._5 },
        { Keys.Number6, ImGuiKey._6 },
        { Keys.Number7, ImGuiKey._7 },
        { Keys.Number8, ImGuiKey._8 },
        { Keys.Number9, ImGuiKey._9 },

        { Keys.F1, ImGuiKey.F1 },
        { Keys.F2, ImGuiKey.F2 },
        { Keys.F3, ImGuiKey.F3 },
        { Keys.F4, ImGuiKey.F4 },
        { Keys.F5, ImGuiKey.F5 },
        { Keys.F6, ImGuiKey.F6 },
        { Keys.F7, ImGuiKey.F7 },
        { Keys.F8, ImGuiKey.F8 },
        { Keys.F9, ImGuiKey.F9 },
        { Keys.F10, ImGuiKey.F10 },
        { Keys.F11, ImGuiKey.F11 },
        { Keys.F12, ImGuiKey.F12 },

        { Keys.ShiftLeft, ImGuiKey.LeftShift },
        { Keys.ShiftRight, ImGuiKey.RightShift },
        { Keys.ControlLeft, ImGuiKey.LeftCtrl },
        { Keys.ControlRight, ImGuiKey.RightCtrl },
        { Keys.AltLeft, ImGuiKey.LeftAlt },
        { Keys.AltRight, ImGuiKey.RightAlt },
        { Keys.SuperLeft, ImGuiKey.LeftSuper },
        { Keys.SuperRight, ImGuiKey.RightSuper },

        { Keys.Minus, ImGuiKey.Minus },
        { Keys.Equal, ImGuiKey.Equal },
        { Keys.LeftBracket, ImGuiKey.LeftBracket },
        { Keys.RightBracket, ImGuiKey.RightBracket },
        { Keys.BackSlash, ImGuiKey.Backslash },
        { Keys.Semicolon, ImGuiKey.Semicolon },
        { Keys.Apostrophe, ImGuiKey.Apostrophe },
        { Keys.Comma, ImGuiKey.Comma },
        { Keys.Period, ImGuiKey.Period },
        { Keys.Slash, ImGuiKey.Slash },
        { Keys.GraveAccent, ImGuiKey.GraveAccent },
    };

    private readonly nint _context;

    private bool _showDemoWindow = false;
    private bool _showLogWindow = true;
    private bool _showInfoWindow = true;

    private float _smoothedFps;

    /// <summary>
    /// Dockspace id used by <see cref="ImGui.DockSpaceOverViewport(uint, ImGuiViewportPtr, ImGuiDockNodeFlags)"/>.
    /// Keeping it stable allows apps to programmatically rebuild the default layout (DockBuilder) and persist it.
    /// </summary>
    public uint DockspaceId { get; } = 0x574F4F4C; // "WOOL"

    public Action? BuildMenuBar { get; set; }
    public Action? BuildDockedUi { get; set; }
    public Action? BuildStatusBar { get; set; }

    public ImGuiController()
    {
        _context = ImGui.CreateContext();
        MakeCurrent();

        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;

        ConfigureFonts(io);

        ImGui.StyleColorsDark();
    }

    private static unsafe void ConfigureFonts(ImGuiIOPtr io)
    {
        io.Fonts.Clear();

        float fontSize = 18.0f;
        string? fontSizeText = Environment.GetEnvironmentVariable("WOOOL_IMGUI_FONT_SIZE");
        if (!string.IsNullOrWhiteSpace(fontSizeText)
            && float.TryParse(fontSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedSize))
        {
            fontSize = Math.Clamp(parsedSize, 12.0f, 40.0f);
        }

        List<string> candidates = new();

        string? fontPath = Environment.GetEnvironmentVariable("WOOOL_IMGUI_FONT");
        if (!string.IsNullOrWhiteSpace(fontPath))
        {
            foreach (string part in fontPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(part);
            }
        }

        bool loaded = false;
        string? loadedPath = null;

        if (OperatingSystem.IsWindows())
        {
            string windowsFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            candidates.Add(Path.Combine(windowsFonts, "simhei.ttf")); // 黑体（ttf，优先）
            candidates.Add(Path.Combine(windowsFonts, "msyh.ttc"));   // 微软雅黑
            candidates.Add(Path.Combine(windowsFonts, "msyhl.ttc"));  // 微软雅黑 Light
            candidates.Add(Path.Combine(windowsFonts, "msyhbd.ttc")); // 微软雅黑 Bold
            candidates.Add(Path.Combine(windowsFonts, "simsun.ttc")); // 宋体
            candidates.Add(Path.Combine(windowsFonts, "Deng.ttf"));   // 等线
            candidates.Add(Path.Combine(windowsFonts, "Dengb.ttf"));  // 等线 Bold

            // 额外兜底（部分非中文系统可能只有日/韩/繁中字库）：
            candidates.Add(Path.Combine(windowsFonts, "msjh.ttc"));    // 微软正黑体
            candidates.Add(Path.Combine(windowsFonts, "msjhl.ttc"));   // 微软正黑体 Light
            candidates.Add(Path.Combine(windowsFonts, "YuGothR.ttc")); // 游ゴシック（日本語）
            candidates.Add(Path.Combine(windowsFonts, "YuGothM.ttc")); // 游ゴシック（日本語）
            candidates.Add(Path.Combine(windowsFonts, "msgothic.ttc")); // MS ゴシック（日本語）
            candidates.Add(Path.Combine(windowsFonts, "malgun.ttf"));   // 맑은 고딕（韩文）
            candidates.Add(Path.Combine(windowsFonts, "malgunbd.ttf")); // 맑은 고딕 Bold（韩文）
        }
        else if (OperatingSystem.IsLinux())
        {
            // 常见 Linux 字体路径（不会强依赖，File.Exists 失败会跳过）。
            candidates.Add("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc");
            candidates.Add("/usr/share/fonts/opentype/noto/NotoSansCJKsc-Regular.otf");
            candidates.Add("/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc");
            candidates.Add("/usr/share/fonts/truetype/wqy/wqy-microhei.ttc");
            candidates.Add("/usr/share/fonts/wqy-microhei/wqy-microhei.ttc");
        }
        else if (OperatingSystem.IsMacOS())
        {
            // 常见 macOS 字体（仅兜底）。
            candidates.Add("/System/Library/Fonts/PingFang.ttc");
            candidates.Add("/System/Library/Fonts/PingFangSC-Regular.otf");
            candidates.Add("/System/Library/Fonts/STHeiti Medium.ttc");
        }

        // NOTE: `GetGlyphRangesChineseSimplifiedCommon` 仅包含约 2500 个常用字，UI 文案里稍微“生僻”一点的字就可能变成问号。
        // 为了避免“中文全是问号”的体验，默认使用 `ChineseFull`；如需更小的字体贴图，可通过环境变量切回 common。
        // - WOOOL_IMGUI_GLYPH_RANGE=full   -> 中文全量字库（默认）
        // - WOOOL_IMGUI_GLYPH_RANGE=common -> 简体中文常用字库（更小更快）
        string glyphRangeMode = Environment.GetEnvironmentVariable("WOOOL_IMGUI_GLYPH_RANGE")?.Trim() ?? string.Empty;
        nint glyphRanges = glyphRangeMode.Equals("common", StringComparison.OrdinalIgnoreCase)
            ? io.Fonts.GetGlyphRangesChineseSimplifiedCommon()
            : io.Fonts.GetGlyphRangesChineseFull();
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                ImFontPtr font = io.Fonts.AddFontFromFileTTF(candidate, fontSize, default, glyphRanges);
                loaded = font.NativePtr != null;
                if (loaded)
                {
                    loadedPath = candidate;
                    break;
                }

                Console.Error.WriteLine($"ImGui 字体加载失败（返回空指针）：{candidate}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ImGui 字体加载失败：{candidate} ({ex.Message})");
            }
        }

        if (!loaded)
        {
            Console.Error.WriteLine("ImGui 未能加载可用中文字体，将回退到默认字体（中文可能显示为问号）。可通过环境变量 WOOOL_IMGUI_FONT 指定字体路径（支持多个路径用 ; 分隔）。");
            io.Fonts.AddFontDefault();
            return;
        }

        if (!string.IsNullOrWhiteSpace(loadedPath))
        {
            Console.Error.WriteLine($"ImGui 字体已加载：{loadedPath} (size={fontSize.ToString(CultureInfo.InvariantCulture)})");
        }
    }

    public void MakeCurrent()
    {
        ImGui.SetCurrentContext(_context);
    }

    public ImDrawDataPtr UpdateAndRender(GlfwInput input, float deltaSeconds, int framebufferWidth, int framebufferHeight)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        MakeCurrent();
        ImGuiIOPtr io = ImGui.GetIO();

        io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : (1.0f / 60.0f);
        io.DisplaySize = new Vector2(framebufferWidth, framebufferHeight);
        io.DisplayFramebufferScale = Vector2.One;

        io.AddMousePosEvent((float)input.MouseX, (float)input.MouseY);
        io.AddMouseButtonEvent(0, input.IsMouseDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, input.IsMouseDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, input.IsMouseDown(MouseButton.Middle));
        io.AddMouseWheelEvent((float)input.ScrollX, (float)input.ScrollY);

        io.AddKeyEvent(ImGuiKey.ModCtrl, input.CtrlDown);
        io.AddKeyEvent(ImGuiKey.ModShift, input.ShiftDown);
        io.AddKeyEvent(ImGuiKey.ModAlt, input.AltDown);
        io.AddKeyEvent(ImGuiKey.ModSuper, input.SuperDown);

        foreach ((Keys glfwKey, ImGuiKey imguiKey) in KeyMap)
        {
            io.AddKeyEvent(imguiKey, input.IsKeyDown(glfwKey));
        }

        for (int i = 0; i < input.CharInputs.Count; i++)
        {
            io.AddInputCharacter(input.CharInputs[i]);
        }

        ImGui.NewFrame();

        BuildUi(framebufferWidth, framebufferHeight);

        ImGui.Render();
        return ImGui.GetDrawData();
    }

    private void BuildUi(int framebufferWidth, int framebufferHeight)
    {
        _ = framebufferWidth;
        _ = framebufferHeight;

        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("窗口"))
            {
                ImGui.MenuItem("信息", null, ref _showInfoWindow);
                ImGui.MenuItem("日志", null, ref _showLogWindow);
                ImGui.MenuItem("ImGui 演示", null, ref _showDemoWindow);
                ImGui.EndMenu();
            }

            try
            {
                BuildMenuBar?.Invoke();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"BuildMenuBar 发生异常: {ex}");
            }

            ImGui.EndMainMenuBar();
        }

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGui.DockSpaceOverViewport(DockspaceId, viewport, ImGuiDockNodeFlags.None);

        try
        {
            BuildDockedUi?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BuildDockedUi 发生异常: {ex}");
        }

        if (_showInfoWindow)
        {
            if (ImGui.Begin("信息", ref _showInfoWindow))
            {
                ImGui.TextUnformatted("WoOOLToOOLsSharp - Vulkan + ImGui.NET");
                ImGui.Separator();
                ImGui.TextUnformatted("这是迁移阶段的最小 UI 验证窗口：");
                ImGui.BulletText("菜单栏");
                ImGui.BulletText("Docking（可拖拽停靠）");
                ImGui.BulletText("日志窗口");

                ImGui.Separator();
                ImGui.TextUnformatted("快捷键：Esc 关闭窗口");
            }
            ImGui.End();
        }

        if (_showLogWindow)
        {
            if (ImGui.Begin("日志", ref _showLogWindow))
            {
                float fps = _smoothedFps;
                if (fps <= 0) fps = 1;
                ImGui.Text($"帧率: {fps:0.0}");
                ImGui.Separator();
                ImGui.TextUnformatted("这里后续会接入：格式解析日志 / 资产加载进度 / 错误栈等。");
            }
            ImGui.End();
        }

        if (_showDemoWindow)
        {
            ImGui.ShowDemoWindow(ref _showDemoWindow);
        }

        try
        {
            BuildStatusBar?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BuildStatusBar 发生异常: {ex}");
        }

        float currentFps = ImGui.GetIO().Framerate;
        if (_smoothedFps <= 0)
        {
            _smoothedFps = currentFps;
        }
        else
        {
            _smoothedFps = (0.9f * _smoothedFps) + (0.1f * currentFps);
        }
    }

    public void Dispose()
    {
        MakeCurrent();
        ImGui.DestroyContext(_context);
    }
}
