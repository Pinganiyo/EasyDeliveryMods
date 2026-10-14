using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// In-game Custom Skin Manager panel.
/// Toggle with F7 (or configured key).
/// Allows viewing loaded skins, hot-reloading from disk, and opening the CustomPlayer folder.
/// </summary>
internal sealed class CustomizationMenu : MonoBehaviour
{
    private bool _visible;
    private Rect _windowRect = new Rect(30f, 30f, 400f, 340f);

    private GUIStyle? _windowStyle;
    private GUIStyle? _headerStyle;
    private GUIStyle? _labelStyle;
    private GUIStyle? _valueStyle;
    private GUIStyle? _infoStyle;
    private bool _stylesBuilt;

    private void Update()
    {
        PlayerCustomization.UpdateTick();

        if (Plugin.CustomMenuKey == null) return;
        if (Input.GetKeyDown(Plugin.CustomMenuKey.Value))
            Toggle();
    }

    internal void Toggle()
    {
        _visible = !_visible;
        if (_visible)
        {
            PlayerCustomization.ReloadTextures();
        }
    }

    private void OnGUI()
    {
        if (!_visible) return;

        BuildStyles();

        // Semi-transparent backdrop
        GUI.color = new Color(0f, 0f, 0f, 0.45f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        _windowRect = GUI.Window(
            id: 0x4543_C002,
            clientRect: _windowRect,
            func: DrawWindow,
            text: "Custom Player Skins  [F7]",
            style: _windowStyle);
    }

    private void DrawWindow(int id)
    {
        float pad = 12f;
        float y = 32f;
        float w = _windowRect.width - pad * 2;

        // Player 1 Status
        GUI.Label(new Rect(pad, y, w, 20f), "PLAYER 1 SKIN", _headerStyle);
        y += 20f;
        string p1Status = PlayerCustomization.HasTextureP1
            ? $"Custom ({PlayerCustomization.TextureP1!.width}x{PlayerCustomization.TextureP1!.height} Point)"
            : "Default (No custom texture found)";
        GUI.Label(new Rect(pad, y, 70f, 20f), "Status:", _labelStyle);
        GUI.Label(new Rect(pad + 75f, y, w - 75f, 20f), p1Status, _valueStyle);
        y += 20f;
        string p1Path = File.Exists(PlayerCustomization.Player1TexturePath)
            ? "CustomPlayer/player1.png"
            : (File.Exists(PlayerCustomization.DefaultTexturePath) ? "CustomPlayer/player.png (Shared)" : "None");
        GUI.Label(new Rect(pad, y, 70f, 20f), "Source:", _labelStyle);
        GUI.Label(new Rect(pad + 75f, y, w - 75f, 20f), p1Path, _valueStyle);
        y += 26f;

        // Player 2 Status
        GUI.Label(new Rect(pad, y, w, 20f), "PLAYER 2 SKIN", _headerStyle);
        y += 20f;
        string p2Status = PlayerCustomization.HasTextureP2
            ? $"Custom ({PlayerCustomization.TextureP2!.width}x{PlayerCustomization.TextureP2!.height} Point)"
            : "Default (No custom texture found)";
        GUI.Label(new Rect(pad, y, 70f, 20f), "Status:", _labelStyle);
        GUI.Label(new Rect(pad + 75f, y, w - 75f, 20f), p2Status, _valueStyle);
        y += 20f;
        string p2Path = File.Exists(PlayerCustomization.Player2TexturePath)
            ? "CustomPlayer/player2.png"
            : (File.Exists(PlayerCustomization.DefaultTexturePath) ? "CustomPlayer/player.png (Shared)" : "None");
        GUI.Label(new Rect(pad, y, 70f, 20f), "Source:", _labelStyle);
        GUI.Label(new Rect(pad + 75f, y, w - 75f, 20f), p2Path, _valueStyle);
        y += 30f;

        // Action Buttons
        if (GUI.Button(new Rect(pad, y, (w - 8f) * 0.5f, 30f), "Reload Skins Now"))
        {
            PlayerCustomization.ReloadTextures();
        }

        if (GUI.Button(new Rect(pad + (w - 8f) * 0.5f + 8f, y, (w - 8f) * 0.5f, 30f), "Open Skins Folder"))
        {
            try
            {
                PlayerCustomization.EnsureDirectoryExists();
                Process.Start(new ProcessStartInfo
                {
                    FileName = PlayerCustomization.CustomPlayerDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[CustomizationMenu] Could not open folder: {ex.Message}");
            }
        }
        y += 38f;

        // Guide / Instructions
        GUI.Label(new Rect(pad, y, w, 70f),
            "Tips:\n" +
            "• Use EasySkin Studio (skin-editor/index.html) to draw or recolor skins.\n" +
            "• Save 'player1.png' for P1, 'player2.png' for P2, or 'player.png' for both.\n" +
            "• Changes automatically hot-reload when saved to CustomPlayer!", _infoStyle);
        y += 74f;

        // Close Button
        if (GUI.Button(new Rect(pad, y, w, 28f), "Close [F7]"))
        {
            _visible = false;
        }

        GUI.DragWindow(new Rect(0, 0, _windowRect.width, 24f));
    }

    private void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _windowStyle = new GUIStyle(GUI.skin.window)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter
        };
        _windowStyle.normal.textColor = new Color(0.95f, 0.85f, 0.3f);

        _headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold
        };
        _headerStyle.normal.textColor = new Color(0.4f, 0.9f, 1f);

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            alignment = TextAnchor.MiddleLeft
        };
        _labelStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

        _valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _valueStyle.normal.textColor = Color.white;

        _infoStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = 10,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true
        };
        _infoStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
    }
}
