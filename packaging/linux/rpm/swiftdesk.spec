Name:           swiftdesk
Version:        1.0.0
Release:        1%{?dist}
Summary:        Cross-platform remote desktop and enterprise support tool

License:        MIT
URL:            https://github.com/sahilyadav-01/SwiftDesk
Source0:        swiftdesk-%{version}.tar.gz

Requires:       libX11, libXtst, pipewire-libs >= 0.3.50, systemd

%description
SwiftDesk provides high-performance remote desktop control with owner consent,
real-time screen sharing, synthetic input, chunked integrity-checked file transfers,
audited remote reboot, and OIDC/MFA enterprise operator authorization.

%install
mkdir -p %{buildroot}/opt/swiftdesk
mkdir -p %{buildroot}/etc/swiftdesk
mkdir -p %{buildroot}%{_unitdir}

install -m 755 publish/RemoteDesk.Client %{buildroot}/opt/swiftdesk/
install -m 755 publish/RemoteDesk.Agent %{buildroot}/opt/swiftdesk/
install -m 755 publish/RemoteDesk.PrivilegedHelper %{buildroot}/opt/swiftdesk/
install -m 644 packaging/linux/systemd/swiftdesk-privileged-reboot.service %{buildroot}%{_unitdir}/

%post
mkdir -p /etc/swiftdesk
chmod 700 /etc/swiftdesk
if [ ! -f /etc/swiftdesk/reboot.key ]; then
    head -c 32 /dev/urandom > /etc/swiftdesk/reboot.key
    chmod 600 /etc/swiftdesk/reboot.key
fi
%systemd_post swiftdesk-privileged-reboot.service

%preun
%systemd_preun swiftdesk-privileged-reboot.service

%postun
%systemd_postun_with_restart swiftdesk-privileged-reboot.service

%files
/opt/swiftdesk/RemoteDesk.Client
/opt/swiftdesk/RemoteDesk.Agent
/opt/swiftdesk/RemoteDesk.PrivilegedHelper
%{_unitdir}/swiftdesk-privileged-reboot.service
%dir %attr(700, root, root) /etc/swiftdesk
