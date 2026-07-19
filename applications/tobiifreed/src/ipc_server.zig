const builtin = @import("builtin");
const std = @import("std");

pub const ForwardFn = *const fn (client_fd: std.posix.fd_t, cmd_type: u8, payload: []const u8, is_ws: bool) void;

pub const supported = builtin.os.tag != .windows;

pub const Server = if (supported)
    @import("server.zig").Server
else
    struct {
        pub fn init(command_callback: ForwardFn) !@This() {
            _ = command_callback;
            return .{};
        }

        pub fn acceptClients(_: *@This()) void {}

        pub fn readCommands(_: *@This()) void {}

        pub fn broadcastGaze(_: *@This(), _: anytype) void {}

        pub fn deinit(_: *@This()) void {}
    };
