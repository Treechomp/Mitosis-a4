"""Movement system for updating entity positions."""
import esper

from ...components.core import Position, Velocity, ChunkPosition
from ....world.world_manager import WorldManager


class MovementProcessor(esper.Processor):
    """Processes entity movement and updates positions."""

    def __init__(self, world_manager: WorldManager, chunk_size: int):
        self.world_manager = world_manager
        self.chunk_size = chunk_size

    def process(self) -> None:
        """Update all entity positions based on velocity."""
        for entity, (pos, vel) in self.world.get_components(Position, Velocity):
            # Calculate new position
            new_x = pos.x + vel.dx
            new_y = pos.y + vel.dy

            # Check if new position is walkable
            if self.world_manager.is_walkable(new_x, new_y):
                pos.x = new_x
                pos.y = new_y

                # Update chunk position if entity has one
                if self.world.has_component(entity, ChunkPosition):
                    chunk_pos = self.world.component_for_entity(entity, ChunkPosition)
                    if chunk_pos.update(pos, self.chunk_size):
                        # Entity changed chunks - update spatial hash
                        self.world_manager.spatial_hash.update(entity, pos.x, pos.y)
            else:
                # Hit obstacle - stop movement
                vel.dx = 0
                vel.dy = 0
