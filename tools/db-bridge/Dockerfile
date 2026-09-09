FROM alpine:3.20
RUN apk add --no-cache openvpn socat ca-certificates iproute2 bind-tools busybox-extras
COPY start.sh /usr/local/bin/start-db-bridge
RUN chmod +x /usr/local/bin/start-db-bridge
ENTRYPOINT ["/usr/local/bin/start-db-bridge"]
