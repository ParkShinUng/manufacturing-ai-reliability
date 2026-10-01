// operations-service: migrate, project, serve (OPERATIONS_API.md §15).
await (await Mair.OperationsService.OperationsHost.BuildAsync(args)).RunAsync();
