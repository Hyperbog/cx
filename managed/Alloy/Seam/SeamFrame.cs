using System;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The per-frame environment the scene and camera slots need and the scene database does not hold:
/// the draw rect, the screen size and the game's UI scale. Three engine reads, resolved once at boot, each a
/// WINDOW property rather than a scene one -- the engine still owns the window, the swap chain and the SDL
/// event loop. Latched once per present, because a per-slot read would be a hundred engine calls a frame.</summary>
internal static unsafe class SeamFrame
{
    private static readonly int* _viewport = (int*)Native.Sym(SymbolMap.GlViewport);
    private static readonly int* _screenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
    private static readonly int* _screenH = (int*)Native.Sym(SymbolMap.ScreenHeight);
    private static GetGuiScaleFn _guiScale;
    private static float _vx, _vy, _vw, _vh, _scale = 1f;
    private static ExtenderLog? _log;

    /// <summary>The game's UI scale as of this frame's latch. 1 until the first latch, which is also what a
    /// client whose <c>GetGUIScale</c> did not resolve reports -- and that case is announced once.</summary>
    internal static float GuiScale => _scale;
    internal static float ReadGuiScale() => _guiScale != null ? _guiScale() : 1f;

    /// <summary>The screen size <c>SetViewPortLight</c> clamps a viewport against.</summary>
    internal static int ScreenW => _screenW != null ? *_screenW : (int)_vw;
    internal static int ScreenH => _screenH != null ? *_screenH : (int)_vh;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _guiScale = EngineFn.GetGuiScale;
        if (_viewport == null)
            log.Always("SeamFrame: GLRender::m_viewport unresolved -- the CX scene database cannot know the "
                     + "draw rect, so every world view's aspect and every MapToScreen answer would be wrong; "
                     + "the seam must stand down (the scene database has nothing to render into)");
        if (_guiScale == null)
            log.Always("SeamFrame: CAurora::GetGUIScale unresolved -- the game's GUI cannot follow the game "
                     + "UI scale, so panels render at scale 1");
        GuiUpscale.Announce(log);
    }


    /// <summary>Latch the frame's rect and UI scale. Called once per present, from the frame boundary.</summary>
    internal static void Latch()
    {
        ReadViewport(out _vx, out _vy, out _vw, out _vh);
        if (_guiScale != null)
        {
            float was = _scale;
            _scale = ReadGuiScale();
            if (_scale != was)
                _log?.Info($"SeamFrame: game GUI scale {was:0.###} -> {_scale:0.###} at screen {ScreenW}x{ScreenH}, "
                           + $"draw rect {_vw}x{_vh}");
        }
        TextFontBake.FollowScale();
    }

    /// <summary>Read current viewport dimensions on the game thread, including before the render latch.</summary>
    internal static void ReadViewport(out float x, out float y, out float w, out float h)
    {
        if (_viewport != null)
        {
            x = _viewport[0]; y = _viewport[1]; w = _viewport[2]; h = _viewport[3];
            return;
        }
        x = y = 0f;
        w = _screenW != null ? *_screenW : 0f;
        h = _screenH != null ? *_screenH : 0f;
    }

    /// <summary>The frame's draw rect, as latched.</summary>
    internal static void Viewport(out float x, out float y, out float w, out float h)
    {
        x = _vx; y = _vy; w = _vw; h = _vh;
    }
}
