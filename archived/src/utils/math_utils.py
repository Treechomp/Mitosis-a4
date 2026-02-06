"""Numba-optimized math utilities for performance-critical calculations."""
import math
import numpy as np
from numba import njit, prange


@njit(cache=True)
def distance_squared(x1: float, y1: float, x2: float, y2: float) -> float:
    """Calculate squared distance between two points (avoids sqrt)."""
    dx = x2 - x1
    dy = y2 - y1
    return dx * dx + dy * dy


@njit(cache=True)
def distance(x1: float, y1: float, x2: float, y2: float) -> float:
    """Calculate distance between two points."""
    return math.sqrt(distance_squared(x1, y1, x2, y2))


@njit(cache=True)
def normalize_vector(dx: float, dy: float) -> tuple[float, float]:
    """Normalize a 2D vector. Returns (0, 0) if magnitude is 0."""
    mag_sq = dx * dx + dy * dy
    if mag_sq < 1e-10:
        return (0.0, 0.0)
    inv_mag = 1.0 / math.sqrt(mag_sq)
    return (dx * inv_mag, dy * inv_mag)


@njit(cache=True, parallel=True)
def batch_distances_squared(
    source_x: float,
    source_y: float,
    target_xs: np.ndarray,
    target_ys: np.ndarray,
) -> np.ndarray:
    """
    Calculate squared distances from one point to many targets.
    Uses parallel processing for large arrays.
    """
    n = len(target_xs)
    result = np.empty(n, dtype=np.float64)
    for i in prange(n):
        dx = target_xs[i] - source_x
        dy = target_ys[i] - source_y
        result[i] = dx * dx + dy * dy
    return result


@njit(cache=True)
def find_nearest_in_range_squared(
    source_x: float,
    source_y: float,
    target_xs: np.ndarray,
    target_ys: np.ndarray,
    target_ids: np.ndarray,
    range_squared: float,
) -> tuple[int, float]:
    """
    Find the nearest target within range.
    Returns (target_id, distance_squared) or (-1, inf) if none found.
    """
    n = len(target_xs)
    nearest_id = -1
    nearest_dist_sq = float('inf')

    for i in range(n):
        dx = target_xs[i] - source_x
        dy = target_ys[i] - source_y
        dist_sq = dx * dx + dy * dy

        if dist_sq < range_squared and dist_sq < nearest_dist_sq:
            nearest_dist_sq = dist_sq
            nearest_id = target_ids[i]

    return (nearest_id, nearest_dist_sq)


@njit(cache=True)
def calculate_flee_vector(
    prey_x: float,
    prey_y: float,
    predator_xs: np.ndarray,
    predator_ys: np.ndarray,
    flee_range_squared: float,
) -> tuple[float, float, bool]:
    """
    Calculate flee direction away from nearby predators.
    Returns (flee_dx, flee_dy, has_threat).
    """
    flee_dx = 0.0
    flee_dy = 0.0
    threat_found = False

    for i in range(len(predator_xs)):
        dx = prey_x - predator_xs[i]
        dy = prey_y - predator_ys[i]
        dist_sq = dx * dx + dy * dy

        if dist_sq < flee_range_squared and dist_sq > 1e-10:
            # Weight by inverse distance for stronger flee from closer threats
            inv_dist = 1.0 / math.sqrt(dist_sq)
            flee_dx += dx * inv_dist
            flee_dy += dy * inv_dist
            threat_found = True

    # Normalize the combined flee vector
    if threat_found:
        flee_dx, flee_dy = normalize_vector(flee_dx, flee_dy)

    return (flee_dx, flee_dy, threat_found)
