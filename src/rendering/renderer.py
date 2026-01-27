"""Main renderer using Arcade."""
import arcade
from arcade import color as colors

from world.chunk import TileType, TILE_COLORS
from world.manager import WorldManager
from rendering.camera import Camera


class Renderer:
    """Handles all rendering using Arcade."""

    def __init__(
        self,
        tile_size: int,
        chunk_size: int,
    ):
        self.tile_size = tile_size
        self.chunk_size = chunk_size

        # Sprite lists for batched rendering
        self.tile_shapes: arcade.ShapeElementList | None = None
        self.entity_sprites: arcade.SpriteList = arcade.SpriteList()

        # Cache for which chunks have been rendered
        self._rendered_chunks: set[tuple[int, int]] = set()

    def render_world(
        self,
        world: WorldManager,
        camera: Camera,
    ) -> None:
        """Render visible world tiles."""
        # Get visible chunk range
        min_cx, min_cy, max_cx, max_cy = camera.get_visible_chunk_range(
            self.chunk_size, self.tile_size
        )

        # Create shape list if needed or if view changed significantly
        visible_chunks = set()
        for cx in range(min_cx, max_cx + 1):
            for cy in range(min_cy, max_cy + 1):
                visible_chunks.add((cx, cy))

        # Rebuild if chunks changed
        if visible_chunks != self._rendered_chunks:
            self._rebuild_tile_shapes(world, camera, min_cx, min_cy, max_cx, max_cy)
            self._rendered_chunks = visible_chunks

        # Draw tiles
        if self.tile_shapes:
            self.tile_shapes.draw()

    def _rebuild_tile_shapes(
        self,
        world: WorldManager,
        camera: Camera,
        min_cx: int,
        min_cy: int,
        max_cx: int,
        max_cy: int,
    ) -> None:
        """Rebuild the tile shape list for visible chunks."""
        shape_list = []

        for cx in range(min_cx, max_cx + 1):
            for cy in range(min_cy, max_cy + 1):
                chunk = world.get_chunk(cx, cy)
                if chunk is None:
                    continue

                # World offset for this chunk (in pixels)
                chunk_world_x = cx * self.chunk_size * self.tile_size
                chunk_world_y = cy * self.chunk_size * self.tile_size

                for local_y in range(chunk.size):
                    for local_x in range(chunk.size):
                        tile_type = chunk.get_tile(local_x, local_y)
                        color = TILE_COLORS.get(tile_type, (255, 0, 255))

                        # Calculate screen position
                        world_px = chunk_world_x + local_x * self.tile_size
                        world_py = chunk_world_y + local_y * self.tile_size

                        screen_x, screen_y = camera.world_to_screen(
                            world_px + self.tile_size / 2,
                            world_py + self.tile_size / 2,
                        )

                        # Create rectangle
                        shape = arcade.create_rectangle_filled(
                            screen_x,
                            screen_y,
                            self.tile_size * camera.zoom,
                            self.tile_size * camera.zoom,
                            color,
                        )
                        shape_list.append(shape)

        self.tile_shapes = arcade.ShapeElementList()
        for shape in shape_list:
            self.tile_shapes.append(shape)

    def render_entities(
        self,
        positions: list[tuple[float, float]],
        renderables: list[tuple[tuple[int, int, int], float, str]],
        camera: Camera,
    ) -> None:
        """Render entities as simple shapes.

        Args:
            positions: List of (x, y) world positions
            renderables: List of (color, size, shape) tuples
            camera: Camera for coordinate conversion
        """
        for (x, y), (color, size, shape) in zip(positions, renderables):
            screen_x, screen_y = camera.world_to_screen(
                x * self.tile_size,
                y * self.tile_size,
            )

            scaled_size = size * camera.zoom

            if shape == "circle":
                arcade.draw_circle_filled(screen_x, screen_y, scaled_size / 2, color)
            elif shape == "square":
                arcade.draw_rectangle_filled(
                    screen_x, screen_y, scaled_size, scaled_size, color
                )
            elif shape == "triangle":
                arcade.draw_triangle_filled(
                    screen_x, screen_y - scaled_size / 2,
                    screen_x - scaled_size / 2, screen_y + scaled_size / 2,
                    screen_x + scaled_size / 2, screen_y + scaled_size / 2,
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
            f"Chunks: {chunk_count}",
            f"Player: ({player_pos[0]:.1f}, {player_pos[1]:.1f})",
        ]

        for text in texts:
            arcade.draw_text(
                text,
                10,
                y,
                colors.WHITE,
                14,
                font_name="Arial",
            )
            y -= line_height

    def clear_cache(self) -> None:
        """Clear the rendering cache (call when camera moves significantly)."""
        self._rendered_chunks.clear()
        self.tile_shapes = None
