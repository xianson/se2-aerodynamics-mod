# Atmosphere - the air model (engine-free)

`AtmosphereModel` (layered barometric atmosphere), `AtmosphereState` (density, pressure, temperature, speed of sound,
viscosity), `CompressibleFlow` (isentropic / shock relations), `AtmosphereTail` (the air's upper tail past the game's
linear generator edge). Pure math, compiled by the offline tests. The game side is `Air/`.
