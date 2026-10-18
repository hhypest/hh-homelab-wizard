FROM alpine:3.24.2@sha256:d56c381f961d307a21b3ca004cf1e3910f106644aefb1f43e654c8a56c4fd395

RUN apk add --no-cache openssh-server=10.3_p1-r1 openssh-sftp-server=10.3_p1-r1 sudo=1.9.17_p2-r2 \
    && adduser -D -u 1000 wizard \
    && mkdir -p /home/wizard/.ssh /run/sshd \
    && chown wizard:wizard /home/wizard/.ssh \
    && chmod 700 /home/wizard/.ssh \
    && printf 'wizard ALL=(root) NOPASSWD: /bin/true\n' > /etc/sudoers.d/wizard \
    && chmod 440 /etc/sudoers.d/wizard

COPY sshd_config entrypoint.sh dispatch.sh preflight.sh /fixture/
RUN chmod 755 /fixture/entrypoint.sh /fixture/dispatch.sh
ENTRYPOINT ["/fixture/entrypoint.sh"]
