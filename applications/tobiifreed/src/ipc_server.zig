const builtin = @import("builtin");

pub const supported = builtin.os.tag != .windows;

pub const Server = if (supported)
    @import("server.zig").Server
else
    struct {
        pub fn init(command_callback: anytype) !@This() {
            _ = command_callback;
            return .{};
        }

        pub fn acceptClients(_: *@This()) void {}

        pub fn readCommands(_: *@This()) void {}

        pub fn broadcastGaze(_: *@This(), _: anytype) void {}

        pub fn deinit(_: *@This()) void {}
    };
