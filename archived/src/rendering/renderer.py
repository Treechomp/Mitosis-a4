"""Optimized renderer using Arcade with batched drawing."""
from collections import deque
import arcade
from arcade import color as colors
from arcade.shape_list import ShapeElementList, create_rectangles_filled_with_colors

from world.chunk import TileType, TILE_COLORS, Chunk
from world.manager import WorldManager


class Renderer:
    """Handles all rendering using batched Arcade shapes with queued building."""

    def __init__(
        self,
        tile_size: int,
        chunk_size: int,
        shapes_per_frame: int = 4,  # Max shapes to build per frame (fast with pre-computed data)
    ):
        self.tile_size = tile_size
        self.chunk_size = chunk_size
        self.shapes_per_frame = shapes_per_frame

        # Cache of ShapeElementLists per chunk: (chunk_x, chunk_y) -> ShapeElementList
        self._chunk_shapes: dict[tuple[int, int], ShapeElementList] = {}

        # Queue for chunks that need shape building
        self._shape_queue: deque[Chunk] = deque()
        self._queued_keys: set[tuple[int, int]] = set()

        # Track camera position for prioritization
        self._camera_chunk: tuple[int, int] = (0, 0)

        # Pre-allocated Text objects for debug HUD (much faster than draw_text)
        self._debug_texts: list[arcade.Text] = [
            arcade.Text("", 10, 700 - i * 20, colors.WHITE, 14)
            for i in range(5)
        ]

    def _build_chunk_shapes(self, chunk: Chunk) -> ShapeElementList:
        """Build a ShapeElementList for a chunk's tiles using pre-computed data."""
        shape_list = ShapeElementList()

        # Use pre-computed render data if available (computed in worker thread)
        if chunk.render_points is not None and chunk.render_colors is not None:
            point_list = chunk.render_points
            color_list = chunk.render_colors
        else:
            # Fallback: compute on main thread (shouldn't happen normally)
            chunk_world_x = chunk.chunk_x * self.chunk_size * self.tile_size
            chunk_world_y = chunk.chunk_y * self.chunk_size * self.tile_size
            half_tile = self.tile_size / 2

            point_list = []
            color_list = []

            for local_y in range(chunk.size):
                for local_x in range(chunk.size):
                    tile_type = chunk.get_tile(local_x, local_y)
                    color = TILE_COLORS.get(tile_type, (255, 0, 255))
                    color_rgba = (color[0], color[1], color[2], 255)

                    world_x = chunk_world_x + local_x * self.tile_size + half_tile
                    world_y = chunk_world_y + local_y * self.tile_size + half_tile

                    point_list.append((world_x - half_tile, world_y + half_tile))
                    point_list.append((world_x + half_tile, world_y + half_tile))
                    point_list.append((world_x + half_tile, world_y - half_tile))
                    point_list.append((world_x - half_tile, world_y - half_tile))
                    color_list.extend([color_rgba, color_rgba, color_rgba, color_rgba])

        # Create batched shape (this is the only OpenGL operation - must be main thread)
        if point_list:
            shape = create_rectangles_filled_with_colors(point_list, color_list)
            shape_list.append(shape)

        return shape_list

    def queue_chunk_shapes(self, chunk: Chunk) -> None:
        """Add a chunk to the shape building queue."""
        key = (chunk.chunk_x, chunk.chunk_y)
        if key not in self._chunk_shapes and key not in self._queued_keys:
            self._shape_queue.append(chunk)
            self._queued_keys.add(key)

    def process_shape_queue(self) -> int:
        """
        Process up to shapes_per_frame chunks from the queue.
        Returns number of shapes built.
        """
        built = 0

        # Sort queue by distance to camera (prioritize nearby chunks)
        if len(self._shape_queue) > 1:
            self._shape_queue = deque(sorted(
                self._shape_queue,
                key=lambda c: max(abs(c.chunk_x - self._camera_chunk[0]),
                                  abs(c.chunk_y - self._camera_chunk[1]))
            ))

        while self._shape_queue and built < self.shapes_per_frame:
            chunk = self._shape_queue.popleft()
            key = (chunk.chunk_x, chunk.chunk_y)
            self._queued_keys.discard(key)

            # Skip if already built (shouldn't happen but safety check)
            if key in self._chunk_shapes:
                continue

            self._chunk_shapes[key] = self._build_chunk_shapes(chunk)
            built += 1

        return built

    def get_pending_shapes_count(self) -> int:
        """Get number of chunks waiting for shape building."""
        return len(self._shape_queue)

    def remove_chunk(self, chunk_x: int, chunk_y: int) -> None:
        """Remove a chunk's cached shapes."""
        key = (chunk_x, chunk_y)
        self._chunk_shapes.pop(key, None)
        self._queued_keys.discard(key)

    def render_world(
        self,
        world: WorldManager,
        camera: "arcade.Camera2D",
    ) -> None:
        """Render visible world tiles using batched drawing."""
        # Update camera chunk for prioritization
        chunk_pixel_size = self.chunk_size * self.tile_size
        self._camera_chunk = (
            int(camera.position[0] // chunk_pixel_size),
            int(camera.position[1] // chunk_pixel_size),
        )

        # Get visible area in world coordinates
        view_left = camera.position[0] - camera.viewport_width / 2 / camera.zoom
        view_bottom = camera.position[1] - camera.viewport_height / 2 / camera.zoom
        view_right = camera.position[0] + camera.viewport_width / 2 / camera.zoom
        view_top = camera.position[1] + camera.viewport_height / 2 / camera.zoom

        # Convert to chunk coordinates
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

                key = (cx, cy)

                # If shapes not ready, queue for building (will appear next frame)
                if key not in self._chunk_shapes:
                    self.queue_chunk_shapes(chunk)
                    continue

                self._chunk_shapes[key].draw()

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
        pending_chunks: int = 0,
    ) -> None:
        """Render debug information overlay using pre-allocated Text objects."""
        pending_shapes = self.get_pending_shapes_count()
        texts = [
            f"FPS: {fps:.1f}",
            f"Entities: {entity_count}",
            f"Chunks: {chunk_count} loaded, {pending_chunks} generating",
            f"Shapes: {len(self._chunk_shapes)} cached, {pending_shapes} building",
            f"Player: ({player_pos[0]:.1f}, {player_pos[1]:.1f})",
        ]

        for i, text in enumerate(texts):
            self._debug_texts[i].text = text
            self._debug_texts[i].draw()

    def clear_cache(self) -> None:
        """Clear the rendering cache."""
        self._chunk_shapes.clear()
        self._shape_queue.clear()
        self._queued_keys.clear()
