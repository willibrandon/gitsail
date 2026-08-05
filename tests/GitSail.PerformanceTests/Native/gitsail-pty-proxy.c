#define _GNU_SOURCE

#include <errno.h>
#include <limits.h>
#include <poll.h>
#include <pty.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>

extern char **environ;

static volatile sig_atomic_t termination_signal;

static void record_signal(int signal_number)
{
    termination_signal = signal_number;
}

static int parse_dimension(const char *text, const char *name)
{
    char *end = NULL;
    errno = 0;
    const long value = strtol(text, &end, 10);
    if (errno != 0 || end == text || *end != '\0' || value <= 0 || value > USHRT_MAX)
    {
        fprintf(stderr, "Invalid %s: %s\n", name, text);
        exit(2);
    }

    return (int)value;
}

static int write_all(int descriptor, const unsigned char *buffer, size_t length)
{
    size_t offset = 0;
    while (offset < length)
    {
        const ssize_t written = write(descriptor, buffer + offset, length - offset);
        if (written > 0)
        {
            offset += (size_t)written;
            continue;
        }

        if (written < 0 && errno == EINTR)
        {
            continue;
        }

        return -1;
    }

    return 0;
}

static int status_to_exit_code(int status)
{
    if (WIFEXITED(status))
    {
        return WEXITSTATUS(status);
    }

    if (WIFSIGNALED(status))
    {
        return 128 + WTERMSIG(status);
    }

    return 1;
}

int main(int argc, char **argv)
{
    if (argc < 4)
    {
        fprintf(stderr, "Usage: gitsail-pty-proxy <width> <height> <executable> [argument ...]\n");
        return 2;
    }

    const int width = parse_dimension(argv[1], "width");
    const int height = parse_dimension(argv[2], "height");
    struct winsize size = {
        .ws_row = (unsigned short)height,
        .ws_col = (unsigned short)width,
        .ws_xpixel = 0,
        .ws_ypixel = 0,
    };

    int master_descriptor = -1;
    const pid_t child_pid = forkpty(&master_descriptor, NULL, NULL, &size);
    if (child_pid < 0)
    {
        perror("forkpty");
        return 1;
    }

    if (child_pid == 0)
    {
        execve(argv[3], &argv[3], environ);
        perror("execve");
        _exit(127);
    }

    struct sigaction action = {0};
    action.sa_handler = record_signal;
    sigemptyset(&action.sa_mask);
    if (sigaction(SIGINT, &action, NULL) < 0 ||
        sigaction(SIGTERM, &action, NULL) < 0 ||
        sigaction(SIGHUP, &action, NULL) < 0)
    {
        perror("sigaction");
        kill(-child_pid, SIGKILL);
        kill(child_pid, SIGKILL);
        close(master_descriptor);
        waitpid(child_pid, NULL, 0);
        return 1;
    }

    unsigned char buffer[8192];
    int status = 0;
    int child_exited = 0;
    int monitor_input = 1;
    while (!child_exited)
    {
        if (termination_signal != 0)
        {
            kill(-child_pid, termination_signal);
            kill(child_pid, termination_signal);
        }

        struct pollfd descriptors[2] = {
            {.fd = master_descriptor, .events = POLLIN, .revents = 0},
            {.fd = monitor_input ? STDIN_FILENO : -1, .events = POLLIN, .revents = 0},
        };
        const int poll_result = poll(descriptors, 2, 20);
        if (poll_result < 0 && errno != EINTR)
        {
            perror("poll");
            kill(-child_pid, SIGKILL);
            kill(child_pid, SIGKILL);
            break;
        }

        if ((descriptors[0].revents & POLLIN) != 0)
        {
            const ssize_t count = read(master_descriptor, buffer, sizeof(buffer));
            if (count > 0 && write_all(STDOUT_FILENO, buffer, (size_t)count) < 0)
            {
                kill(-child_pid, SIGTERM);
                kill(child_pid, SIGTERM);
            }
        }

        if ((descriptors[1].revents & POLLIN) != 0)
        {
            const ssize_t count = read(STDIN_FILENO, buffer, sizeof(buffer));
            if (count > 0)
            {
                if (write_all(master_descriptor, buffer, (size_t)count) < 0 && errno != EIO)
                {
                    perror("write pty");
                }
            }
            else
            {
                monitor_input = 0;
            }
        }
        else if ((descriptors[1].revents & (POLLERR | POLLHUP | POLLNVAL)) != 0)
        {
            monitor_input = 0;
        }

        const pid_t wait_result = waitpid(child_pid, &status, WNOHANG);
        if (wait_result == child_pid)
        {
            child_exited = 1;
        }
        else if (wait_result < 0 && errno != EINTR)
        {
            perror("waitpid");
            break;
        }
    }

    close(master_descriptor);
    if (!child_exited)
    {
        kill(-child_pid, SIGKILL);
        kill(child_pid, SIGKILL);
        if (waitpid(child_pid, &status, 0) < 0)
        {
            return 1;
        }
    }

    return status_to_exit_code(status);
}
