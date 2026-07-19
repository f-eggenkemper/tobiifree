const builtin = @import("builtin");

pub const supported = builtin.os.tag != .windows;

pub const Server = if (supported)
    @import("server.zig").Server
else
    struct {
        pub fn init(_: anytype) !@This() {
            return .{};
        }

        pub fn acceptClients(_: *@This()) void {}

        pub fn readCommands(_: *@This()) void {}

        pub fn deinit(_: *@This()) void {}
    };
