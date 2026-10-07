return args.Length == 0 ? Unit.Run() : args[0] switch
{
    "load" => await Load.Run(args),
    "udp" => await Udp.Run(args),
    "games" => await Games.Run(args),
    _ => Unit.Run(),
};
