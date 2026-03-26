using Mitosis.ECS;

namespace Mitosis.Systems;

/// <summary>
/// Base interface for all ECS systems.
/// </summary>
public interface ISystem
{
    /// <summary>
    /// Process one tick of the simulation.
    /// </summary>
    void Process(EntityManager em);
}
