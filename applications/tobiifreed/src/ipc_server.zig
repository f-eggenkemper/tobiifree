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

fn testForward(_: std.posix.fd_t, _: u8, _: []const u8, _: bool) void {}

test "supported matches target platform" {
    try std.testing.expectEqual(builtin.os.tag != .windows, supported);
}

test "server api is callable on this platform" {
    if (supported) return;

    var server = try Server.init(&testForward);
    server.acceptClients();
    server.readCommands();
    server.broadcastGaze(0);
    server.deinit();
}
