#!/usr/bin/env python3
"""
Standalone FFLogs parser script for Dalamud plugin.
Takes a log file path, report code, and region, outputs parsed data as JSON.

Usage: python fflogs_parser.py <log_file> <report_code> <region>
Output: JSON with 'master' and 'fights' keys

Requires: Node.js installed, parser bundle downloaded.
"""

import sys
import os
import json
import subprocess
import queue
import threading
import time
import tempfile
import urllib.request
import re

# Configuration
OUTPUT_SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'fflogs_parser_bundle.js')
FFLOGS_URL = "https://www.fflogs.com"

def download_parser_if_needed():
    """Download the parser bundle if not present or outdated."""
    if os.path.exists(OUTPUT_SCRIPT):
        # Check if it's recent (less than 24 hours old)
        if time.time() - os.path.getmtime(OUTPUT_SCRIPT) < 86400:
            return True
    
    print(json.dumps({"status": "downloading_parser"}))
    sys.stdout.flush()
    
    try:
        # Fetch the parser page
        ts = int(time.time() * 1000)
        parser_page_url = f"{FFLOGS_URL}/desktop-client/parser?id=1&ts={ts}&metersEnabled=false&liveFightDataEnabled=false"
        
        req = urllib.request.Request(parser_page_url, headers={
            'User-Agent': 'FFLogsUploader/8.17.115'
        })
        
        with urllib.request.urlopen(req) as resp:
            html = resp.read().decode('utf-8')
        
        # Extract parser URL
        match = re.search(r'src="([^"]+parser-ff[^"]+)"', html)
        if not match:
            return False
        
        parser_url = match.group(1)
        
        # Download parser
        with urllib.request.urlopen(parser_url) as resp:
            parser_code = resp.read().decode('utf-8')
        
        # Extract glue code
        glue_match = re.search(r'<script type="text/javascript">([\s\S]*?ipcCollectFights[\s\S]*?)</script>', html)
        glue_code = glue_match.group(1) if glue_match else ""
        
        # Create runner wrapper
        runner_code = '''
const fs = require('fs');
const readline = require('readline');

// Mock browser environment
const window = {
    location: { search: '?id=1&metersEnabled=false&liveFightDataEnabled=false' },
    addEventListener: (type, listener) => { if (type === 'message') global.messageListener = listener; }
};
const document = {
    createElement: () => ({ style: {}, appendChild: () => {} }),
    getElementsByTagName: () => [{ appendChild: () => {} }],
    body: { appendChild: () => {} },
    readyState: 'complete',
    addEventListener: () => {}
};
global.window = window;
global.document = document;
global.self = window;
global.navigator = { userAgent: 'FFLogsPlugin/1.0' };

// IPC output
window.sendToHost = (channel, id, event, data) => {
    console.log('__IPC__:' + JSON.stringify({ channel, id, data }));
};

// Readline for input
const rl = readline.createInterface({ input: process.stdin, output: process.stdout, terminal: false });
rl.on('line', (line) => {
    if (!line.trim()) return;
    try {
        const msg = JSON.parse(line);
        if (global.messageListener) {
            global.messageListener({
                data: msg,
                source: { postMessage: (r) => console.log('__IPC__:' + JSON.stringify(r)) },
                origin: 'emulator'
            });
        }
    } catch (e) {}
});

'''
        
        # Bundle everything
        full_bundle = runner_code + "\n\n" + parser_code + "\n\n" + glue_code
        
        with open(OUTPUT_SCRIPT, 'w', encoding='utf-8') as f:
            f.write(full_bundle)
        
        return True
    except Exception as e:
        print(json.dumps({"error": f"Failed to download parser: {e}"}))
        return False


def run_parser(log_file, report_code, region):
    """Run the Node.js parser and return results."""
    
    if not download_parser_if_needed():
        return {"error": "Parser download failed"}
    
    if not os.path.exists(OUTPUT_SCRIPT):
        return {"error": "Parser script not found"}
    
    # Read log file
    try:
        with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()
    except Exception as e:
        return {"error": f"Failed to read log file: {e}"}
    
    # Start Node.js process
    try:
        proc = subprocess.Popen(
            ['node', OUTPUT_SCRIPT],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1
        )
    except Exception as e:
        return {"error": f"Failed to start Node.js: {e}"}
    
    ipc_queue = queue.Queue()
    
    def read_thread(p, q):
        while True:
            line = p.stdout.readline()
            if not line:
                break
            if line.startswith("__IPC__:"):
                try:
                    data = json.loads(line[8:].strip())
                    q.put(data)
                except:
                    pass
    
    t = threading.Thread(target=read_thread, args=(proc, ipc_queue), daemon=True)
    t.start()
    
    # Initialize parser
    proc.stdin.write(json.dumps({"message": "set-report-code", "id": 0, "reportCode": report_code}) + "\n")
    proc.stdin.flush()
    time.sleep(0.1)
    
    # Parse lines
    region_map = {1: "NA", 2: "EU", 3: "JP", 4: "CN", 5: "KR"}
    region_str = region_map.get(int(region), "NA")
    
    proc.stdin.write(json.dumps({
        "message": "parse-lines",
        "id": 1,
        "lines": [l.rstrip('\n\r') for l in lines],
        "isLive": False,
        "region": region_str,
        "selectedFights": []
    }) + "\n")
    proc.stdin.flush()
    
    # Wait for parsing to complete
    time.sleep(0.5)
    
    # Collect fights
    proc.stdin.write(json.dumps({
        "message": "collect-fights",
        "id": 2,
        "isLive": False,
        "forReport": False
    }) + "\n")
    proc.stdin.flush()
    
    # Wait and collect results
    time.sleep(0.5)
    
    # Collect master info
    proc.stdin.write(json.dumps({
        "message": "collect-master-info",
        "id": 3,
        "reportCode": report_code
    }) + "\n")
    proc.stdin.flush()
    
    time.sleep(0.5)
    
    # Process results
    fights_data = None
    master_data = None
    
    timeout = time.time() + 10  # 10 second timeout
    while time.time() < timeout:
        try:
            msg = ipc_queue.get(timeout=0.1)
            channel = msg.get('channel', '')
            data = msg.get('data', {})
            
            if channel == 'ipc-collect-fights':
                fights_data = data
            elif channel == 'ipc-collect-master-info':
                master_data = data
                
            if fights_data and master_data:
                break
        except queue.Empty:
            continue
    
    # Cleanup
    proc.terminate()
    
    if not fights_data or not master_data:
        return {"error": "Parser did not return complete data", "fights": fights_data, "master": master_data}
    
    return {
        "fights": fights_data,
        "master": master_data
    }


def main():
    if len(sys.argv) < 4:
        print(json.dumps({"error": "Usage: python fflogs_parser.py <log_file> <report_code> <region>"}))
        sys.exit(1)
    
    log_file = sys.argv[1]
    report_code = sys.argv[2]
    region = int(sys.argv[3])
    
    result = run_parser(log_file, report_code, region)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
