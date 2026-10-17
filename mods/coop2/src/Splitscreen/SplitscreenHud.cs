using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// IMGUI overlay that draws the splitscreen dividing line, P1/P2 labels,
/// and native in-game floating world-space interaction markers for Player 2.
/// </summary>
internal sealed class SplitscreenHud : MonoBehaviour
{
    private bool _showHud;
    private GUIStyle? _labelStyle;
    private GUIStyle? _inGameLabelStyle;
    private bool _stylesBuilt;
    private Texture2D? _lineTex;

    internal void SetActive(bool active)
    {
        _showHud = active;
        enabled  = active;
    }

    private void OnGUI()
    {
        if (!_showHud) return;

        BuildOnce();

        bool vertical = Plugin.SplitscreenOrientation?.Value != "Horizontal";
        int w = Screen.width;
        int h = Screen.height;

        if (SplitscreenManager.MergedView)
        {
            // shared camera: no divider and no P1/P2 tags
        }
        else if (vertical)
        {
            // Draw vertical dividing line in the center
            int cx = w / 2;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(cx - 1, 0, 3, h), _lineTex!);
            GUI.color = Color.white;

            // P1 / P2 labels
            DrawTag("P1", 10, 10);
            DrawTag("P2", cx + 10, 10);
        }
        else
        {
            // Horizontal split — divider is a horizontal bar
            int cy = h / 2;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0, cy - 1, w, 3), _lineTex!);
            GUI.color = Color.white;

            DrawTag("P1", 10, 10);
            DrawTag("P2", 10, cy + 10);
        }

        P2Dialog.Draw();
        PackageThrow.Draw();

        // ── Native World-Space Floating Markers for Player 2 ────────────────
        var interaction = SplitscreenManager.GetP2WorldInteraction();
        var p2Cam = SplitscreenManager.P2ViewCamera;

        if (interaction.HasValue && p2Cam != null)
        {
            var data = interaction.Value;
            Vector3 vp = p2Cam.WorldToViewportPoint(data.WorldPosition);

            // Visible if in front of camera and inside Player 2's viewport frustum
            if (vp.z > 0.1f && vp.x >= -0.05f && vp.x <= 1.05f && vp.y >= -0.05f && vp.y <= 1.05f)
            {
                // Map viewport coordinates (0..1) directly to whole-screen GUI coordinates (0..Screen.width, 0..Screen.height)
                float normScreenX = p2Cam.rect.x + Mathf.Clamp01(vp.x) * p2Cam.rect.width;
                float normScreenY = p2Cam.rect.y + Mathf.Clamp01(vp.y) * p2Cam.rect.height;

                float guiX = normScreenX * Screen.width;
                float guiY = (1f - normScreenY) * Screen.height;

                float scale = p2Cam.rect.height * Screen.height / 256f;

                if (data.IsSprite)
                {
                    // Icons from the game's sprite sheet, placed like sHUD.Marker.Draw does
                    var r = data.Sprite;
                    if (data.IsMarker)
                        GameFont.DrawSprite(r, guiX - 1f * scale, guiY - 1f * scale, scale);
                    else
                        GameFont.DrawSprite(r, guiX - Mathf.Floor(r.width / 2f) * scale, guiY - Mathf.Floor(r.height / 2f) * scale, scale);
                }
                else if (!string.IsNullOrEmpty(data.Label))
                {
                    string display = data.Label;

                    if (GameFont.Ready)
                    {
                        // Same bitmap font and placement rule as the HUD's own markers
                        // (centred, text box starts 11 game pixels above the point).
                        GameFont.Draw(GameFont.Translate(display), guiX, guiY - 11f * scale, scale, Color.white, centered: true);
                    }
                    else
                    {
                        GUI.color = Color.white;
                        GUI.Label(new Rect(guiX - 100, guiY - 12, 200, 24), display, _inGameLabelStyle!);
                    }
                }
            }
        }
    }

    private void DrawTag(string text, float x, float y)
    {
        if (GameFont.Ready)
        {
            float scale = Screen.height / 256f;
            GameFont.Draw(text, x, y, scale, new Color(0.95f, 0.85f, 0.2f, 1f), centered: false);
        }
        else
        {
            GUI.Label(new Rect(x, y, 80, 30), text, _labelStyle);
        }
    }

    private void BuildOnce()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _lineTex = Texture2D.whiteTexture;

        Font inGameFont = null;
        foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
        {
            if (font != null && !font.name.Contains("Arial"))
            {
                inGameFont = font;
                break;
            }
        }

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            font = inGameFont,
        };
        _labelStyle.normal.textColor = new Color(0.95f, 0.85f, 0.2f, 1f);

        _inGameLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            font = inGameFont,
        };
        _inGameLabelStyle.normal.textColor = Color.white;
    }
}
