using System;
using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using Mitosis.Utils;
using static Mitosis.ECS.EntityManager;

namespace Mitosis;

/// <summary>
/// Handles player input (movement, sprint) and isometric Camera3D control.
///
/// Camera model: orbit camera positioned above the player's terrain location.
///   - Pan/sprint:  WASD moves the player entity; camera follows smoothly.
///   - Zoom:        +/- keys or scroll wheel change the orthographic Size.
///   - Fixed angle: pitch −45°, yaw 45° (NW overhead isometric).
///   - Orbit:       future extension; yaw/pitch are fields for easy wiring.
/// </summary>
public sealed class PlayerController
{
    private readonly EntityManager _entityManager;
    private int _playerEntity = -1;

    // Settings (set from GameManager exports)
    public float PlayerSpeed           { get; set; } = 1.0f;
    public float PlayerSprintMultiplier{ get; set; } = 3.0f;
    public float ZoomMin               { get; set; } = 0.1f;   // multiplied by TileSize*32
    public float ZoomMax               { get; set; } = 5.0f;   // multiplied by TileSize*32
    public float ZoomSpeed             { get; set; } = 0.15f;
    public int   TileSize              { get; set; } = 16;

    public int PlayerEntity => _playerEntity;

    // --- 3D camera state ---
    // Focus point: the world-space XZ position the camera orbits around (Y always = 0).
    private Vector3 _cameraFocus;

    // Isometric angle: pitch (elevation below horizon) and yaw (horizontal rotation).
    // These can be extended to support mouse-drag orbit later.
    private float _cameraPitch = -45f;  // degrees; negative = looking down
    private float _cameraYaw   =  45f;  // degrees; 45 = NW diagonal view

    // Orthographic size in world units (visible height of the view frustum).
    private float _cameraSize = 512f;

    // Expose current size for LOD calculation in GameManager.
    public float CameraSize => _cameraSize;

    // Distance from focus to camera position; kept proportional to camera size.
    private float CameraDistance => _cameraSize * 2f;

    public PlayerController(EntityManager entityManager)
    {
        _entityManager = entityManager;
    }

    public void SetPlayerEntity(int entity, int tileSize, Camera3D? camera)
    {
        _playerEntity = entity;
        TileSize      = tileSize;

        if (entity >= 0 && _entityManager.IsAlive(entity))
        {
            ref var pos = ref _entityManager.Positions[entity];
            _cameraFocus = PlayerFocusPoint(pos.X, pos.Y);
            if (camera != null)
                ApplyCameraTransform(camera);
        }
    }

    public void HandleInput()
    {
        if (_playerEntity < 0 || !_entityManager.IsAlive(_playerEntity))
            return;

        ref var vel = ref _entityManager.Velocities[_playerEntity];

        float speed = PlayerSpeed;
        if (Input.IsKeyPressed(Key.Shift))
            speed *= PlayerSprintMultiplier;

        // --- Camera-relative movement ---
        // Gather raw input in screen space: forward = toward top of screen, right = toward right.
        float inputForward = 0f;
        float inputRight   = 0f;
        if (Input.IsActionPressed("move_up"))    inputForward += 1f;
        if (Input.IsActionPressed("move_down"))  inputForward -= 1f;
        if (Input.IsActionPressed("move_right")) inputRight   += 1f;
        if (Input.IsActionPressed("move_left"))  inputRight   -= 1f;

        // Normalize so diagonal is not faster than cardinal.
        float len = MathF.Sqrt(inputForward * inputForward + inputRight * inputRight);
        if (len > 1f) { inputForward /= len; inputRight /= len; }

        // Rotate input by camera yaw to get world-XZ movement direction.
        // Godot's LookAt builds camera +X (screen right) = (-cos yaw, sin yaw) in world XZ.
        //   camera forward (horizontal) = (  sin(yaw),  cos(yaw) ) in world (X, Z)
        //   camera right   (horizontal) = ( -cos(yaw),  sin(yaw) ) in world (X, Z)
        float yawRad = Mathf.DegToRad(_cameraYaw);
        float sinY   = MathF.Sin(yawRad);
        float cosY   = MathF.Cos(yawRad);
        float worldDX = inputForward * sinY - inputRight * cosY;   // world +X
        float worldDZ = inputForward * cosY + inputRight * sinY;   // world +Z

        // Convert world-XZ to grid velocity.
        //   world_X =  grid_X * tileSize  →  vel.Dx = worldDX * speed
        //   world_Z = -grid_Y * tileSize  →  vel.Dy = -worldDZ * speed
        vel.Dx = worldDX * speed;
        vel.Dy = -worldDZ * speed;
    }

    public void HandleZoomInput(Camera3D? camera)
    {
        if (camera == null) return;
        if (Input.IsActionJustPressed("zoom_in"))  AdjustZoom(1f / (1f + ZoomSpeed));
        if (Input.IsActionJustPressed("zoom_out")) AdjustZoom(1f + ZoomSpeed);
    }

    public void HandleMouseZoom(InputEvent @event, Camera3D? camera)
    {
        if (camera == null) return;
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed)
        {
            if (mouseEvent.ButtonIndex == MouseButton.WheelUp)
                AdjustZoom(1f / (1f + ZoomSpeed));
            else if (mouseEvent.ButtonIndex == MouseButton.WheelDown)
                AdjustZoom(1f + ZoomSpeed);
        }
    }

    public void UpdateCamera(Camera3D? camera, double delta)
    {
        if (camera == null || _playerEntity < 0 || !_entityManager.IsAlive(_playerEntity))
            return;

        ref var pos = ref _entityManager.Positions[_playerEntity];
        var targetFocus = PlayerFocusPoint(pos.X, pos.Y);

        // Smooth follow
        _cameraFocus = _cameraFocus.Lerp(targetFocus, (float)(5.0 * delta));

        ApplyCameraTransform(camera);
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    /// <summary>Returns the world-space focus point for the player's current position.</summary>
    private Vector3 PlayerFocusPoint(float worldX, float worldY)
    {
        // Use VertexToWorld3D with zero elevation so the camera stays level
        // regardless of terrain height. The smooth row-offset interpolation
        // in VertexToWorld3D prevents X-axis jumps at row boundaries.
        return GridCoordinates.VertexToWorld3D(worldX, worldY, TileSize);
    }

    /// <summary>
    /// Positions and orients the camera based on current focus, pitch, yaw, and size.
    ///
    /// Look direction from the camera toward the focus:
    ///   lookDir = (cos(pitch)*sin(yaw),  sin(pitch),  cos(pitch)*cos(yaw))
    /// Camera sits at focusPoint − lookDir * distance.
    /// </summary>
    private void ApplyCameraTransform(Camera3D camera)
    {
        float pitchRad = Mathf.DegToRad(_cameraPitch);
        float yawRad   = Mathf.DegToRad(_cameraYaw);

        float cosP = MathF.Cos(pitchRad);
        float sinP = MathF.Sin(pitchRad);

        // Unit vector from camera toward focus (i.e. the look direction).
        var lookDir = new Vector3(cosP * MathF.Sin(yawRad), sinP, cosP * MathF.Cos(yawRad));

        camera.Position = _cameraFocus - lookDir * CameraDistance;
        camera.LookAt(_cameraFocus, Vector3.Up);
        camera.Size = _cameraSize;
    }

    /// <summary>Multiply camera size by factor, clamped between ZoomMin and ZoomMax.</summary>
    private void AdjustZoom(float factor)
    {
        float minSize = ZoomMin * TileSize * 32f;
        float maxSize = ZoomMax * TileSize * 32f;
        _cameraSize = Math.Clamp(_cameraSize * factor, minSize, maxSize);
    }
}
