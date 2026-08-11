# QingTransfer

The first QingTransfer checkpoint advertises and discovers v1 DNS-SD peers on
the local network. It deliberately has no pairing, connection, or file data
protocol. The temporary TCP listener only supplies the SRV port and closes any
unexpected connection immediately.
