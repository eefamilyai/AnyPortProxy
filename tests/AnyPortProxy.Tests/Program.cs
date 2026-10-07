return args.Length > 0 && args[0] == "load" ? await Load.Run(args) : Unit.Run();
