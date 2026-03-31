using System;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Renders plugin settings inside the Remix developer menu "Plugin" tab.
    /// Registered as a draw callback via <see cref="RemixImGui"/>.
    /// </summary>
    public class RemixSettingsUI
    {
        private readonly ManualLogSource _log;
        private readonly UnityRemixPlugin _plugin;
        private bool _initialized;

        // Cached state for ImGui controls (avoids per-frame alloc)
        private float _maxRenderDistance;
        private float _lightIntensityMultiplier;
        private int _targetFPS;
        private bool _enableGameGeometry;
        private bool _enableDistanceCulling;
        private bool _enableVisibilityCulling;
        private bool _enableLights;
        private bool _captureStaticMeshes;
        private bool _captureSkinnedMeshes;
        private bool _captureTextures;
        private bool _captureMaterials;
        private bool _enableSceneScan;

        public RemixSettingsUI(ManualLogSource log, UnityRemixPlugin plugin)
        {
            _log = log;
            _plugin = plugin;
        }

        /// <summary>
        /// Called from the Remix render thread when the "Plugin" tab is active.
        /// Draws directly into the tab content area — no Begin/End window needed.
        /// </summary>
        public void Draw(IntPtr userData)
        {
            if (!_initialized)
            {
                SyncFromConfig();
                _initialized = true;
            }

            DrawRenderingSection();
            DrawLightingSection();
            DrawPerformanceSection();
            DrawDebugSection();

            RemixImGui.Separator();
            RemixImGui.Spacing();

            if (RemixImGui.Button("Save Settings", 130, 0))
                _plugin.SaveConfig();

            RemixImGui.SameLine();
            if (RemixImGui.Button("Refresh", 80, 0))
                SyncFromConfig();
        }

        private void DrawRenderingSection()
        {
            if (!RemixImGui.CollapsingHeader("Rendering", RemixImGui.TreeNodeFlags_DefaultOpen))
                return;

            if (RemixImGui.Checkbox("Game Geometry", ref _enableGameGeometry))
                _plugin.SetConfig("EnableGameGeometry", _enableGameGeometry);

            if (RemixImGui.Checkbox("Distance Culling", ref _enableDistanceCulling))
                _plugin.SetConfig("EnableDistanceCulling", _enableDistanceCulling);

            if (_enableDistanceCulling)
            {
                RemixImGui.Indent();
                if (RemixImGui.SliderFloat("Max Distance", ref _maxRenderDistance, 10f, 10000f))
                    _plugin.SetConfig("MaxRenderDistance", _maxRenderDistance);
                RemixImGui.Unindent();
            }

            if (RemixImGui.Checkbox("Visibility Culling", ref _enableVisibilityCulling))
                _plugin.SetConfig("UseVisibilityCulling", _enableVisibilityCulling);

            if (RemixImGui.Checkbox("Scene Scan", ref _enableSceneScan))
                _plugin.SetConfig("EnableSceneScan", _enableSceneScan);
        }

        private void DrawLightingSection()
        {
            if (!RemixImGui.CollapsingHeader("Lighting", RemixImGui.TreeNodeFlags_DefaultOpen))
                return;

            if (RemixImGui.Checkbox("Enable Lights", ref _enableLights))
                _plugin.SetConfig("EnableLights", _enableLights);

            if (RemixImGui.SliderFloat("Intensity Multiplier", ref _lightIntensityMultiplier, 0.01f, 100f))
                _plugin.SetConfig("IntensityMultiplier", _lightIntensityMultiplier);
        }

        private void DrawPerformanceSection()
        {
            if (!RemixImGui.CollapsingHeader("Performance"))
                return;

            if (RemixImGui.DragInt("Target FPS", ref _targetFPS, 1, 0, 500))
                _plugin.SetConfig("TargetFPS", _targetFPS);

            RemixImGui.Text(_targetFPS == 0 ? "(Uncapped)" : "");
        }

        private void DrawDebugSection()
        {
            if (!RemixImGui.CollapsingHeader("Debug Toggles"))
                return;

            if (RemixImGui.Checkbox("Static Meshes", ref _captureStaticMeshes))
                _plugin.SetConfig("CaptureStaticMeshes", _captureStaticMeshes);
            if (RemixImGui.Checkbox("Skinned Meshes", ref _captureSkinnedMeshes))
                _plugin.SetConfig("CaptureSkinnedMeshes", _captureSkinnedMeshes);
            if (RemixImGui.Checkbox("Textures", ref _captureTextures))
                _plugin.SetConfig("CaptureTextures", _captureTextures);
            if (RemixImGui.Checkbox("Materials", ref _captureMaterials))
                _plugin.SetConfig("CaptureMaterials", _captureMaterials);
        }

        private void SyncFromConfig()
        {
            _enableGameGeometry = _plugin.GetConfigBool("EnableGameGeometry");
            _enableDistanceCulling = _plugin.GetConfigBool("EnableDistanceCulling");
            _maxRenderDistance = _plugin.GetConfigFloat("MaxRenderDistance");
            _enableVisibilityCulling = _plugin.GetConfigBool("UseVisibilityCulling");
            _enableSceneScan = _plugin.GetConfigBool("EnableSceneScan");
            _enableLights = _plugin.GetConfigBool("EnableLights");
            _lightIntensityMultiplier = _plugin.GetConfigFloat("IntensityMultiplier");
            _targetFPS = _plugin.GetConfigInt("TargetFPS");
            _captureStaticMeshes = _plugin.GetConfigBool("CaptureStaticMeshes");
            _captureSkinnedMeshes = _plugin.GetConfigBool("CaptureSkinnedMeshes");
            _captureTextures = _plugin.GetConfigBool("CaptureTextures");
            _captureMaterials = _plugin.GetConfigBool("CaptureMaterials");
        }

        public void ResetState() { _initialized = false; }
    }
}
