FROM alpine:3.24.2@sha256:d56c381f961d307a21b3ca004cf1e3910f106644aefb1f43e654c8a56c4fd395 AS ssh-base

RUN apk add --no-cache openssh-server=10.3_p1-r1 openssh-sftp-server=10.3_p1-r1 sudo=1.9.17_p2-r2 \
    && adduser -D -u 1000 wizard \
    && mkdir -p /home/wizard/.ssh /run/sshd \
    && chown wizard:wizard /home/wizard/.ssh \
    && chmod 700 /home/wizard/.ssh \
    && printf 'wizard ALL=(root) NOPASSWD: /bin/true\n' > /etc/sudoers.d/wizard \
    && chmod 440 /etc/sudoers.d/wizard

COPY sshd_config entrypoint.sh dispatch.sh preflight.sh paths.sh docker-control.sh /fixture/
RUN chmod 755 /fixture/entrypoint.sh /fixture/dispatch.sh /fixture/docker-control.sh
ENTRYPOINT ["/fixture/entrypoint.sh"]

FROM ssh-base AS ssh-docker
RUN apk add --no-cache docker-engine=29.8.2-r1 docker-cli=29.8.2-r1 docker-cli-compose=5.1.4-r2 \
    && (grep -q '^docker:' /etc/group || addgroup -S docker) \
    && addgroup wizard docker \
    && test -x /usr/libexec/docker/cli-plugins/docker-compose
ENTRYPOINT ["/fixture/entrypoint.sh", "docker"]

# Default target retains the unprivileged fixture without Docker prerequisites.
FROM ssh-base AS ssh-only
