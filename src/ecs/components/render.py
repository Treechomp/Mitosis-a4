"""Rendering-related ECS components."""
from dataclasses import dataclass


@dataclass
class Renderable:
    """Marks an entity as renderable with visual properties."""

    color: tuple[int, int, int] = (255, 255, 255)  # RGB color
    size: float = 8.0  # Size in pixels
    shape: str = "circle"  # "circle", "square", "triangle"
    layer: int = 1  # Render order (higher = on top)


@dataclass
class Visible:
    """Tag component - entity is currently visible on screen."""

    sprite_index: int = -1  # Index in the sprite batch (-1 = not in batch)
