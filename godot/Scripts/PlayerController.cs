using Godot;
using Mitosis.Components;
using Mitosis.ECS;
using static Mitosis.ECS.EntityManager;

namespace Mitosis;

/// <summary>
/// Handles player input (movement, sprint, zoom) and camera following.
/// </summary>
public sealed class PlayerController
{
    private readonly EntityManager _entityManager;
    private int _playerEntity = -1;
    private Vector2 _cameraTarget;

    // Settings (set from GameManager exports)
    public float PlayerSpeed { get; set; } = 1.0f;
    public float PlayerSprintMultiplier { get; set; } = 3.0f;
    public float ZoomMin { get; set; } = 0.1f;
    public float ZoomMax { get; set; } = 5.0f;
    public float ZoomSpeed { get; set; } = 0.15f;
    public int TileSize { get; set; } = 16;

    public int PlayerEntity => _playerEntity;

    public PlayerController(EntityManager entityManager)
    {
        _entityManager = entityManager;
    }

    public void SetPlayerEntity(int entity, int tileSize, Camera2D? camera)
    {
        _playerEntity = entity;
        TileSize = tileSize;
        if (entity >= 0 && _entityManager.IsAlive(entity))
        {
            ref var pos = ref _entityManager.Positions[entity];
            _cameraTarget = new Vector2(pos.X * TileSize, pos.Y * TileSize);
            if (camera != null)
                camera.Position = _cameraTarget;
        }
    }

    public void HandleInput()
    {
        if (_playerEntity < 0 || !_entityManager.IsAlive(_playerEntity))
            return;

        ref var vel = ref _entityManager.Velocities[_playerEntity];

        // Calculate effective speed (with sprint modifier)
        float speed = PlayerSpeed;
        if (Input.IsKeyPressed(Key.Shift))
            speed *= PlayerSprintMultiplier;

        vel.Dx = 0;
        vel.Dy = 0;

        if (Input.IsActionPressed("move_up")) vel.Dy = -speed;
        if (Input.IsActionPressed("move_down")) vel.Dy = speed;
        if (Input.IsActionPressed("move_left")) vel.Dx = -speed;
        if (Input.IsActionPressed("move_right")) vel.Dx = speed;

        // Normalize diagonal movement
        if (vel.Dx != 0 && vel.Dy != 0)
        {
            vel.Dx *= 0.707f;
            vel.Dy *= 0.707f;
        }
    }

    public void HandleZoomInput(Camera2D? camera)
    {
        if (camera == null) return;
        if (Input.IsActionJustPressed("zoom_in"))
            ApplyZoom(camera, 1f + ZoomSpeed);
        if (Input.IsActionJustPressed("zoom_out"))
            ApplyZoom(camera, 1f - ZoomSpeed);
    }

    public void HandleMouseZoom(InputEvent @event, Camera2D? camera)
    {
        if (camera == null) return;
        if (@event is InputEventMouseButton mouseEvent && mouseEvent.Pressed)
        {
            if (mouseEvent.ButtonIndex == MouseButton.WheelUp)
                ApplyZoom(camera, 1f + ZoomSpeed);
            else if (mouseEvent.ButtonIndex == MouseButton.WheelDown)
                ApplyZoom(camera, 1f - ZoomSpeed);
        }
    }

    public void ApplyZoom(Camera2D camera, float factor)
    {
        var minZoom = new Vector2(ZoomMin, ZoomMin);
        var maxZoom = new Vector2(ZoomMax, ZoomMax);
        camera.Zoom = (camera.Zoom * factor).Clamp(minZoom, maxZoom);
    }

    public void UpdateCamera(Camera2D? camera, double delta)
    {
        if (camera == null || _playerEntity < 0 || !_entityManager.IsAlive(_playerEntity))
            return;

        ref var pos = ref _entityManager.Positions[_playerEntity];
        _cameraTarget = new Vector2(pos.X * TileSize, pos.Y * TileSize);

        // Smooth camera follow
        camera.Position = camera.Position.Lerp(_cameraTarget, (float)(5.0 * delta));
    }
}
