"""Exercise the C# adapter against isolated fault-configured fixtures."""
import argparse
from pathlib import Path
import subprocess
import tempfile
import threading
from http.server import ThreadingHTTPServer
from mock_plex import Library,handler

for scenario in ["revoked","denied","absent","multi","album","faults"]:
    with tempfile.TemporaryDirectory(prefix="crate-scenario-") as d:
        options=argparse.Namespace(revoked=scenario=="revoked",deny_writes=scenario=="denied",no_station=scenario=="absent",multiple=scenario=="multi",lose_ack=False,partial_scan=scenario=="faults",no_range=scenario=="faults",deny_radio=scenario=="faults")
        library=Library(Path(d),8,options)
        with ThreadingHTTPServer(("127.0.0.1",0),handler(library)) as server:
            thread=threading.Thread(target=server.serve_forever,daemon=True)
            thread.start()
            try:
                subprocess.run(["dotnet","tests/bin/Release/net10.0/Checks.dll","--scenario",scenario,str(server.server_port)],check=True)
            finally:
                server.shutdown()
                thread.join()
