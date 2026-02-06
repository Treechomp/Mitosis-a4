"""Camera system for viewport management."""
from dataclasses import dataclass


@dataclass
class Camera:
    """Camera for viewport positioning."""

    # Camera position (world coordinates, centered)
    x: float = 0.0
    y: float = 0.0

    # Viewport size (pixels)
    viewport_width: int = 1280
    viewport_height: int = 720

    # Zoom level (1.0 = normal)
    zoom: float = 1.0

    # World bounds
    world_width: float = 2048.0
    world_height: float = 2048.0

    def follow(self, target_x: float, target_y: float, lerp: float = 0.1) -> None:
        """Smoothly follow a target position."""
        self.x += (target_x - self.x) * lerp
        self.y += (target_y - self.y) * lerp
        self._clamp_to_bounds()

    def set_position(self, x: float, y: float) -> None:
        """Set camera position directly."""
        self.x = x
        self.y = y
        self._clamp_to_bounds()

    def _clamp_to_bounds(self) -> None:
        """Clamp camera to world bounds."""
        half_view_w = (self.viewport_width / 2) / self.zoom
        half_view_h = (self.viewport_height / 2) / self.zoom

        self.x = max(half_view_w, min(self.world_width - half_view_w, self.x))
        self.y = max(half_view_h, min(self.world_height - half_view_h, self.y))

    def world_to_screen(self, world_x: float, world_y: float) -> tuple[float, float]:
        """Convert world coordinates to screen coordinates."""
        screen_x = (world_x - self.x) * self.zoom + self.viewport_width / 2
        screen_y = (world_y - self.y) * self.zoom + self.viewport_height / 2
        return screen_x, screen_y

    def screen_to_world(self, screen_x: float, screen_y: float) -> tuple[float, float]:
        """Convert screen coordinates to world coordinates."""
        world_x = (screen_x - self.viewport_width / 2) / self.zoom + self.x
        world_y = (screen_y - self.viewport_height / 2) / self.zoom + self.y
        return world_x, world_y

    def get_visible_bounds(self) -> tuple[float, float, float, float]:
        """Get visible world bounds (left, bottom, right, top)."""
        half_w = (self.viewport_width / 2) / self.zoom
        half_h = (self.viewport_height / 2) / self.zoom
        return (
            self.x - half_w,
            self.y - half_h,
            self.x + half_w,
            self.y + half_h,
        )

    def get_visible_chunk_range(self, chunk_size: int, tile_size: int) -> tuple[int, int, int, int]:
        """Get range of visible chunks (min_x, min_y, max_x, max_y)."""
        left, bottom, right, top = self.get_visible_bounds()

        # Convert pixel bounds to tile bounds, then to chunk bounds
        tiles_per_chunk = chunk_size

        min_chunk_x = max(0, int(left / tile_size / tiles_per_chunk))
        min_chunk_y = max(0, int(bottom / tile_size / tiles_per_chunk))
        max_chunk_x = int(right / tile_size / tiles_per_chunk) + 1
        max_chunk_y = int(top / tile_size / tiles_per_chunk) + 1

        return min_chunk_x, min_chunk_y, max_chunk_x, max_chunk_y
