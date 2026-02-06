"""Spatial hash grid for efficient entity queries."""
from collections import defaultdict
from dataclasses import dataclass, field


@dataclass
class SpatialHash:
    """Spatial hash grid for fast proximity queries."""

    cell_size: float = 32.0  # Size of each cell (should match chunk size)

    # Map of (cell_x, cell_y) -> set of entity IDs
    _grid: dict[tuple[int, int], set[int]] = field(
        default_factory=lambda: defaultdict(set)
    )

    # Map of entity_id -> (cell_x, cell_y) for fast removal
    _entity_cells: dict[int, tuple[int, int]] = field(default_factory=dict)

    def _get_cell(self, x: float, y: float) -> tuple[int, int]:
        """Get the cell coordinates for a position."""
        return (int(x // self.cell_size), int(y // self.cell_size))

    def insert(self, entity_id: int, x: float, y: float) -> None:
        """Insert an entity at a position."""
        cell = self._get_cell(x, y)

        # Remove from old cell if exists
        if entity_id in self._entity_cells:
            old_cell = self._entity_cells[entity_id]
            if old_cell != cell:
                self._grid[old_cell].discard(entity_id)

        # Add to new cell
        self._grid[cell].add(entity_id)
        self._entity_cells[entity_id] = cell

    def remove(self, entity_id: int) -> None:
        """Remove an entity from the grid."""
        if entity_id in self._entity_cells:
            cell = self._entity_cells[entity_id]
            self._grid[cell].discard(entity_id)
            del self._entity_cells[entity_id]

    def update(self, entity_id: int, x: float, y: float) -> None:
        """Update an entity's position (same as insert)."""
        self.insert(entity_id, x, y)

    def query_radius(
        self, x: float, y: float, radius: float
    ) -> list[int]:
        """Get all entity IDs within radius of a point."""
        # Calculate cell range to check
        min_cell_x = int((x - radius) // self.cell_size)
        max_cell_x = int((x + radius) // self.cell_size)
        min_cell_y = int((y - radius) // self.cell_size)
        max_cell_y = int((y + radius) // self.cell_size)

        result = []
        for cx in range(min_cell_x, max_cell_x + 1):
            for cy in range(min_cell_y, max_cell_y + 1):
                result.extend(self._grid.get((cx, cy), set()))

        return result

    def query_cell(self, cell_x: int, cell_y: int) -> set[int]:
        """Get all entity IDs in a specific cell."""
        return self._grid.get((cell_x, cell_y), set())

    def get_entity_cell(self, entity_id: int) -> tuple[int, int] | None:
        """Get the cell an entity is in."""
        return self._entity_cells.get(entity_id)

    def clear(self) -> None:
        """Clear all entities from the grid."""
        self._grid.clear()
        self._entity_cells.clear()
