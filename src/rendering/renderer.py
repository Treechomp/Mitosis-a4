"""Optimized renderer using Arcade with batched drawing."""
import arcade
from arcade import color as colors
from arcade.shape_list import ShapeElementList, create_rectangles_filled_with_colors

from world.chunk import TileType, TILE_COLORS, Chunk
from world.manager import WorldManager


class Renderer:
    """Handles all rendering using batched Arcade shapes."""

    def __init__(
        self,
        tile_size: int,
        chunk_size: int,
    ):
        self.tile_size = tile_size
        self.chunk_size = chunk_size
        # Cache of ShapeElementLists per chunk: (chunk_x, chunk_y) -> ShapeElementList
        self._chunk_shapes: dict[tuple[int, int], ShapeElementList] = {}
        # Track which chunks need rebuilding
        self._dirty_chunks: set[tuple[int, int]] = set()

    def _build_chunk_shapes(self, chunk: Chunk) -> ShapeElementList:
        """Build a ShapeElementList for a chunk's tiles."""
        shape_list = ShapeElementList()

        # Calculate world position for this chunk
        chunk_world_x = chunk.chunk_x * self.chunk_size * self.tile_size
        chunk_world_y = chunk.chunk_y * self.chunk_size * self.tile_size

        half_tile = self.tile_size / 2

        # Build lists of points and colors for batch creation
        point_list = []
        color_list = []

        for local_y in range(chunk.size):
            for local_x in range(chunk.size):
                tile_type = chunk.get_tile(local_x, local_y)
                color = TILE_COLORS.get(tile_type, (255, 0, 255))
                # Add alpha channel
                color_rgba = (color[0], color[1], color[2], 255)

                # World position of tile center
                world_x = chunk_world_x + local_x * self.tile_size + half_tile
                world_y = chunk_world_y + local_y * self.tile_size + half_tile

                # Four corners of rectangle (must go around, not diagonal!)
                top_left = (world_x - half_tile, world_y + half_tile)
                top_right = (world_x + half_tile, world_y + half_tile)
                bottom_right = (world_x + half_tile, world_y - half_tile)
                bottom_left = (world_x - half_tile, world_y - half_tile)

                point_list.extend([top_left, top_right, bottom_right, bottom_left])
                # Need 4 colors per rectangle (one per corner vertex)
                color_list.extend([color_rgba, color_rgba, color_rgba, color_rgba])

        # Create batched shape
        if point_list:
            shape = create_rectangles_filled_with_colors(point_list, color_list)
            shape_list.append(shape)

        return shape_list

    def get_chunk_shapes(self, chunk: Chunk) -> ShapeElementList:
        """Get or create the ShapeElementList for a chunk."""
        key = (chunk.chunk_x, chunk.chunk_y)

        if key not in self._chunk_shapes or key in self._dirty_chunks:
            self._chunk_shapes[key] = self._build_chunk_shapes(chunk)
            self._dirty_chunks.discard(key)

        return self._chunk_shapes[key]

    def mark_chunk_dirty(self, chunk_x: int, chunk_y: int) -> None:
        """Mark a chunk as needing rebuild."""
        self._dirty_chunks.add((chunk_x, chunk_y))

    def remove_chunk(self, chunk_x: int, chunk_y: int) -> None:
        """Remove a chunk's cached shapes."""
        key = (chunk_x, chunk_y)
        self._chunk_shapes.pop(key, None)
        self._dirty_chunks.discard(key)

    def render_world(
        self,
        world: WorldManager,
        camera: "arcade.Camera2D",
    ) -> None:
        """Render visible world tiles using batched drawing."""
        # Get visible area in world coordinates
        view_left = camera.position[0] - camera.viewport_width / 2 / camera.zoom
        view_bottom = camera.position[1] - camera.viewport_height / 2 / camera.zoom
        view_right = camera.position[0] + camera.viewport_width / 2 / camera.zoom
        view_top = camera.position[1] + camera.viewport_height / 2 / camera.zoom

        # Convert to chunk coordinates
        chunk_pixel_size = self.chunk_size * self.tile_size
        min_cx = int(view_left // chunk_pixel_size) - 1
        max_cx = int(view_right // chunk_pixel_size) + 1
        min_cy = int(view_bottom // chunk_pixel_size) - 1
        max_cy = int(view_top // chunk_pixel_size) + 1

        # Draw each visible chunk
        for cx in range(min_cx, max_cx + 1):
            for cy in range(min_cy, max_cy + 1):
                chunk = world.get_chunk(cx, cy)
                if chunk is None:
                    continue

                shape_list = self.get_chunk_shapes(chunk)
                shape_list.draw()

    def render_entities(
        self,
        positions: list[tuple[float, float]],
        renderables: list[tuple[tuple[int, int, int], float, str]],
    ) -> None:
        """Render entities as simple shapes (world coordinates)."""
        for (x, y), (color, size, shape) in zip(positions, renderables):
            # Convert tile position to world pixels
            world_x = x * self.tile_size
            world_y = y * self.tile_size

            if shape == "circle":
                arcade.draw_circle_filled(world_x, world_y, size / 2, color)
            elif shape == "square":
                arcade.draw_rect_filled(
                    arcade.XYWH(world_x, world_y, size, size), color
                )
            elif shape == "triangle":
                arcade.draw_triangle_filled(
                    world_x, world_y - size / 2,
                    world_x - size / 2, world_y + size / 2,
                    world_x + size / 2, world_y + size / 2,
                    color,
                )

    def render_debug_info(
        self,
        fps: float,
        entity_count: int,
        chunk_count: int,
        player_pos: tuple[float, float],
    ) -> None:
        """Render debug information overlay."""
        y = 700
        line_height = 20

        texts = [
            f"FPS: {fps:.1f}",
            f"Entities: {entity_count}",
            f"Chunks loaded: {chunk_count}",
            f"Chunks cached: {len(self._chunk_shapes)}",
            f"Player: ({player_pos[0]:.1f}, {player_pos[1]:.1f})",
        ]

        for text in texts:
            arcade.draw_text(
                text,
                10,
                y,
                colors.WHITE,
                14,
            )
            y -= line_height

    def clear_cache(self) -> None:
        """Clear the rendering cache."""
        self._chunk_shapes.clear()
        self._dirty_chunks.clear()
