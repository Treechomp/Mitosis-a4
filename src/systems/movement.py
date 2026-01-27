"""Movement system for updating entity positions."""
import esper

from components import Position, Velocity, ChunkPosition
from world.manager import WorldManager


class MovementProcessor(esper.Processor):
    """Processes entity movement and updates positions."""

    def __init__(self, world_manager: WorldManager, chunk_size: int):
        self.world_manager = world_manager
        self.chunk_size = chunk_size

    def process(self) -> None:
        """Update all entity positions based on velocity."""
        for entity, (pos, vel) in self.world.get_components(Position, Velocity):
            new_x = pos.x + vel.dx
            new_y = pos.y + vel.dy

            if self.world_manager.is_walkable(new_x, new_y):
                pos.x = new_x
                pos.y = new_y

                if self.world.has_component(entity, ChunkPosition):
                    chunk_pos = self.world.component_for_entity(entity, ChunkPosition)
                    if chunk_pos.update(pos, self.chunk_size):
                        self.world_manager.spatial_hash.update(entity, pos.x, pos.y)
            else:
                vel.dx = 0
                vel.dy = 0
