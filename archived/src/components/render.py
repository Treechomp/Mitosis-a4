"""Rendering-related ECS components."""
from dataclasses import dataclass


@dataclass
class Renderable:
    """Marks an entity as renderable with visual properties."""

    color: tuple[int, int, int] = (255, 255, 255)
    size: float = 8.0
    shape: str = "circle"  # "circle", "square", "triangle"
    layer: int = 1


@dataclass
class Visible:
    """Tag component - entity is currently visible on screen."""

    sprite_index: int = -1
