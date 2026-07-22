#!/usr/bin/env -S uv run --script
#
# /// script
# requires-python = ">=3.12"
# dependencies = [
#   "pyftpdlib>=1.5.10",
# ]
# ///

import os
import sys
import shutil
import glob
import shlex
import subprocess
import datetime
import tempfile
import pathlib
import getpass
import time
import hashlib
import threading

def die(msg):
  print(msg)
  sys.exit(1)

def glob_for_nonempty_files(root_dir, glob_str):
  '''We commonly check for "did the user download 'tool-*-.extension", and we don't want to accept 0-byte files as "yes the user did it" '''
  results = []
  for file in glob.glob(glob_str, root_dir=root_dir, recursive=True):
    if not str(file).startswith(root_dir):
      file = os.path.join(root_dir, file)

    if os.path.isfile(file) and os.path.getsize(file) > 0:
      results.append(file)

    if not os.path.isfile(file):
      print(f'Warning: {file} is not a file!')
    if os.path.isfile(file) and os.path.getsize(file) < 1:
      print(f'Warning: {file} is an empty (0-byte) file. Please ensure your download completed?')
  return results

def glob_for_nonempty_file_fatal(root_dir, glob_str, pre_die_msg):
  results = glob_for_nonempty_files(root_dir, glob_str)
  if len(results) <= 0:
    print(pre_die_msg.strip())
    die(f'Found 0 files matching the above criteria, please create/fetch the file!')
  if len(results) > 1:
    print(pre_die_msg.strip())
    die(f'Found more than one file matching the above criteria, please delete duplicates until the desired one remains!\nDiscovered matching files: {results}')
  # Safety: the above checks prove len(results) == 0
  return results[0]

def pretty_cmd(*cmd, **kwargs):
  debug_cmd_txt = shlex.join(cmd)
  print(f'> {debug_cmd_txt}')
  subprocess.run(list(cmd), **kwargs)

def ask_user_yn_question(question_str):
  while True:
    yn = input(question_str)
    yn = yn.strip().lower()
    if yn == 'y' or yn == 'yes':
      return True
    if yn == 'n' or yn == 'no':
      return True

    print(f'Unknown response "{yn}", please answer with one of y/yes/n/no (ctrl+c to terminate this script)')

def _derive_vars_path(code_path):
    """
    Try to infer matching OVMF_VARS file from OVMF_CODE file.
    """
    directory = os.path.dirname(code_path)
    filename = os.path.basename(code_path)
    candidates = []
    # 1. Direct substitution: CODE -> VARS
    if "CODE" in filename:
        candidates.append(filename.replace("CODE", "VARS"))
    # 2. 4M variant normalization
    candidates.append(filename.replace(".4m.fd", ".fd").replace("CODE", "VARS"))
    # 3. Generic fallback
    candidates.append("OVMF_VARS.fd")
    candidates.append("OVMF_VARS_4M.fd")
    for c in candidates:
        full = os.path.join(directory, c)
        if os.path.exists(full):
            return full
    return None

def ovmf_to_qemu_args(code_path: str):
    """
    Given OVMF_CODE path, return QEMU args for correct firmware usage.
    Returns list of strings suitable for subprocess.
    """

    if not os.path.exists(code_path):
        raise FileNotFoundError(code_path)
    directory = os.path.dirname(code_path)
    filename = os.path.basename(code_path)
    args = []
    vars_path = _derive_vars_path(code_path)
    # Heuristic: if we have a VARS file use pflash mode (preferred)
    if vars_path:
        # Assume the vars file is immutable, copy to OS dir and send the modifiable copy to our args.
        os_temp_dir_vars_file = os.path.join(tempfile.gettempdir(), 'av-switchyard-testbed-MA3-'+os.path.basename(vars_path))
        if not os.path.exists(os_temp_dir_vars_file):
          shutil.copy(vars_path, os_temp_dir_vars_file)
        args += [
            "-drive", f"if=pflash,format=raw,readonly=on,file={code_path}",
            "-drive", f"if=pflash,format=raw,file={os_temp_dir_vars_file}",
        ]
    else:
        # fallback: legacy mode
        args += [
            "-bios", code_path
        ]
    return args


class FtpShare:
    """
    Serve a host directory to the VM over FTP, in-process.

    Replaces the old "build a FAT32 .img on the host and attach it as a USB
    drive" approach (which had host-page-cache vs. guest coherency problems and
    needed sudo/losetup/parted/mkfs). Files are read live from disk per request,
    and the read+write user makes it two-way (the guest can upload logs back to
    the host).

    Networking: with QEMU user-mode networking the guest reaches the host at the
    gateway 10.0.2.2, which SLIRP forwards to the host's loopback — so we bind to
    127.0.0.1 (keeps this writable FTP off the LAN) and advertise 10.0.2.2 to the
    guest. FTP MUST be passive here: in active mode the server connects back to
    the guest, which SLIRP blocks. Hence masquerade_address + a fixed
    passive_ports range, all reachable from the guest as 10.0.2.2:<port> without
    any QEMU hostfwd. (If your libslirp routes 10.0.2.2 somewhere other than
    loopback and the guest can't connect, change host to '0.0.0.0'.)
    """
    def __init__(self, root, host='127.0.0.1', port=2121,
                 user='test', password='test',
                 masquerade='10.0.2.2', passive_ports=range(50000, 50021)):
        from pyftpdlib.authorizers import DummyAuthorizer
        from pyftpdlib.handlers import FTPHandler
        from pyftpdlib.servers import FTPServer

        os.makedirs(root, exist_ok=True)

        authorizer = DummyAuthorizer()
        # perm 'elradfmwMT' = full read + write (list/retrieve + store/mkdir/
        # delete/rename/append/…) so the guest can both fetch builds and push logs.
        authorizer.add_user(user, password, root, perm='elradfmwMT')

        handler = FTPHandler
        handler.authorizer = authorizer
        handler.masquerade_address = masquerade
        handler.passive_ports = list(passive_ports)
        handler.banner = 'johnny-appleseed testbed FTP'

        self.server = FTPServer((host, port), handler)
        self.host = host
        self.port = port
        self.user = user
        self.password = password
        self.masquerade = masquerade
        self.root = root
        self._thread = None

    def start(self):
        self._thread = threading.Thread(
            target=self.server.serve_forever, name='ftp-share', daemon=True)
        self._thread.start()
        print(f'FTP share serving {self.root} on {self.host}:{self.port}')

    def stop(self):
        try:
            self.server.close_all()
        except Exception:
            pass

def deterministic_mac(seed):
    h = hashlib.sha256(str(seed).encode()).digest()

    # QEMU/KVM safe prefix
    mac = [
        0x52, 0x54,           # QEMU OUI
        h[0] & 0x7F,          # ensure unicast (clear multicast bit)
        h[1],
        h[2],
        h[3],
    ]

    return ":".join(f"{b:02x}" for b in mac)

#################### MAIN ####################

testbed_folder = os.path.dirname(os.path.realpath(__file__))

req_bins = [
  'qemu-system-x86_64', 'qemu-img',
]

for b in req_bins:
  if shutil.which(b) is None:
    die(f'Cannot find required binary {b}, please install and ensure containing folder is on your $PATH')

vm_data_folder = os.path.join(testbed_folder, 'vm-data-windows')
os.makedirs(vm_data_folder, exist_ok=True)

# Setup step 1: Do we have a windows 10 iso image to do initial install with?
# If not, instruct user to grab one (automated processes change every 6 months -_-)
install_iso = glob_for_nonempty_file_fatal(
  vm_data_folder, '*.iso',
  f'''
Please download a windows installer .iso file from a site such as
 - https://www.microsoft.com/en-us/software-download/windows10ISO
and place it under the folder {vm_data_folder}
'''
)

# Step step 2: have a .qcow2 for the VM, we can do this ourselves with qemu-img
vm_qcow2s = glob_for_nonempty_files(vm_data_folder, '*[wW]indows*.qcow2')
if len(vm_qcow2s) > 1:
  die(f'We have found 2 or more VM hard drive files, please delete the one you do not plan to use! Discovered qcow2 files: {vm_qcow2s}')
if len(vm_qcow2s) < 1:
  qemu_img_exe = shutil.which('qemu-img')
  vm_qcow2 = os.path.join(vm_data_folder, 'Windows-Test-VM.qcow2') # Assignment: Default qcow2 name
  pretty_cmd(
    qemu_img_exe, 'create', '-f', 'qcow2', vm_qcow2, '135G',
    cwd=vm_data_folder
  )

# Safety: Above checks ensure we should have at least one .qcow2 file.
vm_qcow2s = glob_for_nonempty_files(vm_data_folder, '*.qcow2')
if len(vm_qcow2s) < 1:
  die(f'Failed to find any .qcow2 files under {vm_data_folder}, please inspect command output above for the issue and grab a developer.')
vm_qcow2 = vm_qcow2s[0]

# Grab our required QEMU details from the host
qemu_system_exe = shutil.which('qemu-system-x86_64')
ovmf_code_fd_file = os.environ.get('OVMF_CODE_FILE', None)
canidate_firmware_names = [
  'OVMF_CODE.fd', 'OVMF_CODE.4m.fd'
]
if ovmf_code_fd_file is None:
  # Glob /usr/share for a few file names, using the first
  for canidate_firmware_name in canidate_firmware_names:
    found_fd_files = glob_for_nonempty_files('/usr/share', f'**/{canidate_firmware_name}')
    if len(found_fd_files) > 0:
      ovmf_code_fd_file = found_fd_files[0]
      break
if ovmf_code_fd_file is None or not os.path.exists(ovmf_code_fd_file):
  die(f'''
Cannot find a copy of OVMF_CODE, please ensure edk2 or a similar package is installed.
By default we scan /usr/share for any of the following file names: {canidate_firmware_names}
You may manually specify a location to the file by assigning the environment variable OVMF_CODE_FILE
Current value OVMF_CODE_FILE={ovmf_code_fd_file}
''')


vm_is_installed_flag_file = os.path.join(vm_data_folder, 'FLAG-windows-vm-install-completed.txt')
if not os.path.exists(vm_is_installed_flag_file):
  print(f'VM needs to be installed, launching install instance.')
  print(f'Please install the OS, then close the VM once you are done and return here.')

  pretty_cmd(
    qemu_system_exe,
      '-enable-kvm',
      '-m',       '8192',
      '-smp',     '4',
      '-cpu',     'host',
      '-machine', 'q35',
      *ovmf_to_qemu_args(ovmf_code_fd_file),
      '-drive',   f'file={vm_qcow2},format=qcow2,if=ide',
      '-cdrom',   f'{install_iso}',
      '-boot',    'order=d,menu=on', # prefer cd drive as boot target
# NO INTERNET FOR YOU! - forces local account setups
      '-nic', 'none',
#      '-netdev',  'user,id=net0',
#      '-device',  'e1000,netdev=net0',
      '-device',  'qemu-xhci',
      '-device',  'usb-tablet',
      '-vga',     'std',
      '-display', 'gtk',
  cwd=vm_data_folder)

  os_was_installed = ask_user_yn_question(f'Did OS install complete to your satisfaction? ')
  if not os_was_installed:
    die(f'Exiting because OS was not installed, re-run this script to launch VM in install mode again when you are ready.')

  with open(vm_is_installed_flag_file, 'w') as fd:
    fd.write(f'User completed install at {datetime.datetime.now()}')

if not os.path.exists(vm_is_installed_flag_file):
  die(f'Exiting because OS was not installed, re-run this script to launch VM in install mode again when you are ready.')

print(f'OS install is complete, we see the flag file {vm_is_installed_flag_file}')

# Expose the whole ./dist/ tree to the VM over FTP (replaces the old USB .img),
# so any target's artifacts can be picked from inside the guest, and logs can be
# copied back out to the host by uploading into dist/_from_vm/.
dist_folder = os.path.abspath(os.path.join(testbed_folder, '..', 'dist'))
os.makedirs(dist_folder, exist_ok=True)
from_vm_folder = os.path.join(dist_folder, '_from_vm')
os.makedirs(from_vm_folder, exist_ok=True)

ftp = FtpShare(dist_folder)
ftp.start()

guest_url = f'ftp://{ftp.user}:{ftp.password}@{ftp.masquerade}:{ftp.port}/'
print()
print('┌─ Test artifacts are shared over FTP ' + ('─' * 31))
print(f'│  Host folder    : {dist_folder}')
print(f'│  From the VM    : {guest_url}')
print( '│  In Explorer    : paste that URL into the address bar (uses passive mode)')
print( '│  Do NOT use ftp.exe — it is active-mode and the VM NAT blocks it')
print(f'│  Copy logs out  : upload files into /_from_vm/  →  {from_vm_folder}')
print( '│  PowerShell upload example (run inside the VM):')
print(f'│    $c=New-Object Net.WebClient; $c.Credentials=New-Object Net.NetworkCredential("{ftp.user}","{ftp.password}")')
print(f'│    $c.UploadFile("ftp://{ftp.masquerade}:{ftp.port}/_from_vm/log.txt","C:\\path\\to\\log.txt")')
print('└' + ('─' * 67))
print()

try:
  pretty_cmd(
    qemu_system_exe,
      '-enable-kvm',
      '-m',       str(int(1024 * 16)), # 16gb ram
      '-smp',     '4',
      '-cpu',     'host',
      '-machine', 'q35',
      *ovmf_to_qemu_args(ovmf_code_fd_file),
      '-drive',   f'file={vm_qcow2},format=qcow2,if=ide',
      '-netdev',  'user,id=net0',
      '-device',  'e1000,netdev=net0',
      '-device',  'qemu-xhci',
      '-device',  'usb-tablet',
      '-vga',     'none',
      # '-device',  'qxl-vga'
      '-device',  'virtio-vga-gl',
      '-display', 'gtk,gl=on,show-menubar=off',

      # Requires the qemu-xhci device above, list ALL game controller vendor and device IDs we use here
      '-device', 'usb-host,vendorid=0x045e,productid=0x028e',

  cwd=vm_data_folder)
finally:
  ftp.stop()


