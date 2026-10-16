"""Optional synthetic mail transport is restricted to a disposable internal Docker network."""
from dataclasses import dataclass
import re
import uuid
from contracts import Deadline, execute
from docker_lab import remove_container, worker_command


@dataclass(frozen=True)
# Optional transport probes own one isolated service and internal network.
class MailService:
    network: str
    container: str
    image_digest: str


def service_command(image: str, name: str, network: str) -> list[str]:
    options = ("-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 "
               "-Dgreenmail.users=operator:synthetic@lab.test")
    return ["docker", "run", "--detach", "--pull=never", "--name", name, "--network", network,
            "--network-alias", "greenmail", "--user", "10001:10001", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges",
            "--pids-limit", "128", "--memory", "512m", "--cpus", "1", "--tmpfs", "/tmp:rw,nosuid,nodev,size=64m",
            "--env", "GREENMAIL_OPTS=" + options, image]


def start_mail(image: str, deadline: Deadline) -> MailService:
    if not re.fullmatch(r"[a-zA-Z0-9./:_-]+@sha256:[a-f0-9]{64}", image):
        raise ValueError("GreenMail requires an explicitly pinned registry image digest")
    network = "k-lani-mail-" + uuid.uuid4().hex
    name = network + "-service"
    created = execute(["docker", "network", "create", "--internal", network], deadline)
    if created.exit_code:
        raise ValueError("Could not create isolated internal mail network")
    try:
        result = execute(service_command(image, name, network), deadline)
        if result.exit_code:
            raise ValueError("Pinned GreenMail service could not start")
        return MailService(network, name, image)
    except BaseException:
        stop_mail(MailService(network, name, image), deadline)
        raise


def stop_mail(service: MailService, deadline: Deadline):
    remove_container(service.container, deadline)
    if deadline.remaining() > 0:
        execute(["docker", "network", "rm", service.network], Deadline(min(5, deadline.remaining())))


def transport_probe(worker_image: str, greenmail_image: str, deadline: Deadline) -> dict:
    service = start_mail(greenmail_image, deadline)
    name = "k-lani-mail-probe-" + uuid.uuid4().hex
    marker = "synthetic-" + uuid.uuid4().hex
    try:
        command = worker_command(worker_image, name, network=service.network)
        command.extend(["python3", "-c", transport_code(), marker])
        result = execute(command, Deadline(min(30, deadline.require(10))))
        if result.exit_code or marker not in result.output:
            raise ValueError("SMTP/IMAP round trip did not preserve the synthetic message")
        return {"status": "passed", "image_digest": greenmail_image, "network": "internal; no published ports",
                "detail": "SMTP delivery and independently retrieved IMAP message matched"}
    finally:
        remove_container(name, deadline)
        stop_mail(service, deadline)


def transport_code() -> str:
    return '''import email.message, imaplib, smtplib, sys, time
marker = sys.argv[1]
message = email.message.EmailMessage()
message["From"] = "sender@lab.test"
message["To"] = "operator@lab.test"
message["Subject"] = marker
message.set_content(marker)
for attempt in range(40):
    try:
        with smtplib.SMTP("greenmail", 3025, timeout=1) as smtp:
            smtp.send_message(message)
        break
    except OSError:
        time.sleep(0.1)
else:
    raise SystemExit(3)
with imaplib.IMAP4("greenmail", 3143, timeout=5) as mailbox:
    mailbox.login("operator", "synthetic")
    mailbox.select("INBOX")
    status, identifiers = mailbox.search(None, "ALL")
    if status != "OK" or not identifiers[0]:
        raise SystemExit(3)
    status, contents = mailbox.fetch(identifiers[0].split()[-1], "(RFC822)")
    if status != "OK":
        raise SystemExit(3)
    sys.stdout.buffer.write(contents[0][1])
'''
