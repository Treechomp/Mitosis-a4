"""Core ECS components for position and movement."""
from dataclasses import dataclass


@dataclass
class Position:
    """World position in tile coordinates."""

    x: float
    y: float


@dataclass
class Velocity:
    """Movement velocity in tiles per tick."""

    dx: float = 0.0
    dy: float = 0.0


@dataclass
class ChunkPosition:
    """Cached chunk coordinates for spatial queries."""

    chunk_x: int = 0
    chunk_y: int = 0

    def update(self, pos: Position, chunk_size: int) -> bool:
        """Update chunk position from world position. Returns True if changed."""
        new_x = int(pos.x // chunk_size)
        new_y = int(pos.y // chunk_size)
        if new_x != self.chunk_x or new_y != self.chunk_y:
            self.chunk_x = new_x
            self.chunk_y = new_y
            return True
        return False
