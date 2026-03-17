#pragma warning disable
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using Keen.Game2.Simulation.Utils;
global using Keen.VRage.Core.Game.Components;
global using Keen.VRage.Core.Game.Data;
global using Keen.VRage.Core.Game.Systems;
global using Keen.VRage.DCS.Accessors;
global using Keen.VRage.DCS.Components;
global using Keen.VRage.Library.Collections.Readers;
global using Keen.VRage.Library.Definitions;
global using Keen.VRage.Library.Diagnostics;
global using Keen.VRage.Library.Extensions;
global using Keen.VRage.Library.Mathematics;
global using Keen.VRage.Library.Memory;
global using Keen.VRage.Library.Threading;
global using Keen.VRage.Library.Units;
global using Keen.VRage.Library.Utils;
global using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere;

global using Buffer = Keen.VRage.Library.Memory.Buffer;
global using TypeExtensions = Keen.VRage.Library.Reflection.TypeExtensions;
global using TaskCompletionSource = Keen.VRage.Library.Threading.TaskCompletionSource;
global using TaskCanceledException = System.Threading.Tasks.TaskCanceledException;
