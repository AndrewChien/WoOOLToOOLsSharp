using System;
using System.Collections.Generic;
using Silk.NET.GLFW;

namespace WoOOLToOOLsSharp.Rendering.Vulkan;

public sealed unsafe class GlfwInput : IDisposable, IAsyncDisposable
{
    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;

    private readonly GlfwCallbacks.KeyCallback _keyCallback;
    private readonly GlfwCallbacks.MouseButtonCallback _mouseButtonCallback;
    private readonly GlfwCallbacks.CursorPosCallback _cursorPosCallback;
    private readonly GlfwCallbacks.ScrollCallback _scrollCallback;
    private readonly GlfwCallbacks.CharCallback _charCallback;

    private readonly HashSet<Keys> _keysDown = new();
    private readonly HashSet<MouseButton> _mouseButtonsDown = new();
    private readonly List<uint> _charInputs = new();

    public double MouseX { get; private set; }
    public double MouseY { get; private set; }
    public double ScrollX { get; private set; }
    public double ScrollY { get; private set; }

    public IReadOnlyCollection<Keys> KeysDown => _keysDown;
    public IReadOnlyCollection<MouseButton> MouseButtonsDown => _mouseButtonsDown;
    public IReadOnlyList<uint> CharInputs => _charInputs;

    public bool CtrlDown => _keysDown.Contains(Keys.ControlLeft) || _keysDown.Contains(Keys.ControlRight);
    public bool ShiftDown => _keysDown.Contains(Keys.ShiftLeft) || _keysDown.Contains(Keys.ShiftRight);
    public bool AltDown => _keysDown.Contains(Keys.AltLeft) || _keysDown.Contains(Keys.AltRight);
    public bool SuperDown => _keysDown.Contains(Keys.SuperLeft) || _keysDown.Contains(Keys.SuperRight);

    public GlfwInput(Glfw glfw, WindowHandle* window)
    {
        _glfw = glfw ?? throw new ArgumentNullException(nameof(glfw));
        _window = window;

        _keyCallback = OnKey;
        _mouseButtonCallback = OnMouseButton;
        _cursorPosCallback = OnCursorPos;
        _scrollCallback = OnScroll;
        _charCallback = OnChar;

        _glfw.SetKeyCallback(_window, _keyCallback);
        _glfw.SetMouseButtonCallback(_window, _mouseButtonCallback);
        _glfw.SetCursorPosCallback(_window, _cursorPosCallback);
        _glfw.SetScrollCallback(_window, _scrollCallback);
        _glfw.SetCharCallback(_window, _charCallback);
    }

    public void BeginFrame()
    {
        ScrollX = 0;
        ScrollY = 0;
        _charInputs.Clear();
    }

    public bool IsKeyDown(Keys key) => _keysDown.Contains(key);

    public bool IsMouseDown(MouseButton button) => _mouseButtonsDown.Contains(button);

    private void OnKey(WindowHandle* window, Keys key, int scanCode, InputAction action, KeyModifiers mods)
    {
        _ = window;
        _ = scanCode;
        _ = mods;

        if (key == Keys.Unknown)
        {
            return;
        }

        if (action == InputAction.Press)
        {
            _keysDown.Add(key);
            if (key == Keys.Escape)
            {
                _glfw.SetWindowShouldClose(_window, true);
            }
        }
        else if (action == InputAction.Release)
        {
            _keysDown.Remove(key);
        }
    }

    private void OnMouseButton(WindowHandle* window, MouseButton button, InputAction action, KeyModifiers mods)
    {
        _ = window;
        _ = mods;

        if (action == InputAction.Press)
        {
            _mouseButtonsDown.Add(button);
        }
        else if (action == InputAction.Release)
        {
            _mouseButtonsDown.Remove(button);
        }
    }

    private void OnCursorPos(WindowHandle* window, double xpos, double ypos)
    {
        _ = window;
        MouseX = xpos;
        MouseY = ypos;
    }

    private void OnScroll(WindowHandle* window, double offsetX, double offsetY)
    {
        _ = window;
        ScrollX += offsetX;
        ScrollY += offsetY;
    }

    private void OnChar(WindowHandle* window, uint codepoint)
    {
        _ = window;
        if (codepoint == 0)
        {
            return;
        }
        _charInputs.Add(codepoint);
    }

    public void Dispose()
    {
        _keysDown.Clear();
        _mouseButtonsDown.Clear();
        _charInputs.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
