// The Operations API host. Routes, projectors and the readiness gate arrive in Phase 4 steps 3-5;
// step 1 builds only what ADR-0024's group B conditions need to be checked against.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Run();
