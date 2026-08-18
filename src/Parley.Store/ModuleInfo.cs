// Enables Dapper.AOT for every Dapper call site in this assembly. Without it the
// generator emits DAP005 ("candidate Dapper methods detected, but none have Dapper.AOT
// enabled") and the calls fall back to runtime IL emit, which defeats IsAotCompatible.
[module: Dapper.DapperAot]
