using EcsTypes = Wit.Tecs.Ecs.Ecs;

namespace Wit.Tecs.Example;

/// <summary>
/// Guest-side implementation of the exported functions.
/// This is what a WASM plugin author writes to define ECS systems.
/// </summary>
public static partial class Guest
{
    // Store system indices for the run-system callback
    private const uint MovementSystemIndex = 0;
    private const uint SpawnSystemIndex = 1;

    public static partial void Setup(EcsTypes.App app)
    {
        // Create a movement system that reads velocity and writes position
        var movement = new EcsTypes.System("movement");
        movement.AddQuery(
        [
            EcsTypes.QueryTerm.CreateMut("position"),
            EcsTypes.QueryTerm.CreateRef("velocity"),
        ]);

        // Create a spawn system that uses commands to create entities
        var spawn = new EcsTypes.System("spawn");
        spawn.AddCommands();

        // Order: spawn runs before movement
        spawn.Before(movement);

        // Register both systems in the Update stage
        app.AddSystems(EcsTypes.StageLabel.CreateUpdate(), [movement, spawn]);
    }

    public static partial void RunSystem(uint index, EcsTypes.Query? query, EcsTypes.Commands? commands)
    {
        switch (index)
        {
            case MovementSystemIndex:
                RunMovement(query!);
                break;
            case SpawnSystemIndex:
                RunSpawn(commands!);
                break;
        }
    }

    private static void RunMovement(EcsTypes.Query query)
    {
        // Use batch accessors to get all positions and velocities at once
        while (query.Iter())
        {
            var positions = Imports.GetPositions(query, 0);
            var velocities = Imports.GetVelocities(query, 1);

            // Apply velocity to position for each entity in the batch
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i].X += velocities[i].X;
                positions[i].Y += velocities[i].Y;
            }

            // Write back the updated positions
            Imports.SetPositions(query, 0, positions);
        }
    }

    private static void RunSpawn(EcsTypes.Commands commands)
    {
        // Spawn a new entity with position and velocity
        var entity = commands.Spawn();
        var id = entity.Id();

        Imports.CommandsSetPosition(commands, id, new Position { X = 0, Y = 0 });
        Imports.CommandsSetVelocity(commands, id, new Velocity { X = 1.0f, Y = 0.5f });
    }
}
