using System.Numerics;
using Aero.Core.Grids;
using Aero.Core.Math;

namespace Aero.Core.Components;

/// <summary>
/// Creates IAeroBlockComponent instances for blocks based on their DefinitionId.
/// Standalone version — no SE2 reflection, matches on Block.DefinitionId strings.
/// </summary>
public class BlockComponentFactory
{
    private readonly Dictionary<string, Func<BlockInfo, IAeroBlockComponent>> _factories = new();

    /// <summary>
    /// Register a factory that fires when a block's DefinitionId matches.
    /// </summary>
    public void Register(string definitionId, Func<BlockInfo, IAeroBlockComponent> factory)
    {
        _factories[definitionId] = factory;
    }

    /// <summary>
    /// Try to create an aero component for a single block.
    /// Returns null if no factory matches this block's DefinitionId.
    /// </summary>
    public IAeroBlockComponent? TryCreate(Block block, float blockSize)
    {
        if (blockSize <= 0 || block.DefinitionId == null)
            return null;

        if (_factories.TryGetValue(block.DefinitionId, out var factory))
        {
            var info = BlockInfo.FromBlock(block, blockSize);
            return factory(info);
        }

        return null;
    }

    /// <summary>
    /// Scan every block in the grid and create aero components for all matches.
    /// </summary>
    public void CreateAll(Grid grid, float blockSize, AeroComponentRegistry target)
    {
        foreach (var (_, block) in grid.Blocks)
        {
            var comp = TryCreate(block, blockSize);
            if (comp != null)
                target.Add(comp);
        }
    }
}
