"""第三方 Modbus 从站（pymodbus），供互通测试 PymodbusInteropTests 启动。

本脚本只用 pymodbus 自己的协议实现，不引用本仓库的任何代码，因此能发现"客户端与本仓库的模拟从站犯了同一个错误"的问题。

标准输出（每行一条，供测试读取）：
    READY                 服务器开始监听
    REQ <站号> <功能码>    收到一个请求（十进制）

数据（站号 1 与 2 各一份，互不影响）：
    线圈 C0..C1999         初始全为 0
    离散输入 DI0..DI1999   DI[i] = (i % 3 == 0)
    保持寄存器 HR0..HR999  初始全为 0；站号 2 的 HR0 为 0x0202
    输入寄存器 IR0..IR199  IR[i] = i * 3 + 1
"""

from __future__ import annotations

import argparse
import asyncio
import functools
import sys

from pymodbus import FramerType, pymodbus_apply_logging_config
from pymodbus.constants import ExcCodes
from pymodbus.server import ModbusSerialServer, ModbusTcpServer, ModbusUdpServer
from pymodbus.simulator import DataType, SimData, SimDevice


def emit(line: str) -> None:
    print(line, flush=True)


async def reject_codes(rejected: set[int], function_code, _start, _address, _count, _registers, _values):
    """模拟不支持某些功能码的设备：对这些功能码返回异常码 01（Illegal Function）。"""
    return ExcCodes.ILLEGAL_FUNCTION if function_code in rejected else None


def device(device_id: int, rejected: set[int]) -> SimDevice:
    holding = [0] * 1000
    if device_id == 2:
        holding[0] = 0x0202
    return SimDevice(
        id=device_id,
        simdata=(
            [SimData(address=0, values=[False] * 2000, datatype=DataType.BITS)],
            [SimData(address=0, values=[i % 3 == 0 for i in range(2000)], datatype=DataType.BITS)],
            [SimData(address=0, values=holding, datatype=DataType.REGISTERS)],
            [SimData(address=0, values=[i * 3 + 1 for i in range(200)], datatype=DataType.REGISTERS)],
        ),
        action=functools.partial(reject_codes, rejected),
    )


def trace_pdu(sending: bool, pdu):
    if not sending:
        emit(f"REQ {pdu.dev_id} {pdu.function_code}")
    return pdu


async def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--transport", choices=["tcp", "udp", "serial"], required=True)
    parser.add_argument("--framer", choices=["socket", "rtu"], required=True)
    parser.add_argument("--port", type=int, default=502, help="TCP / UDP 端口")
    parser.add_argument("--serial", default="", help="串口名，例如 COM21")
    parser.add_argument("--baud", type=int, default=9600)
    parser.add_argument("--reject", default="", help="逗号分隔的功能码（十进制），对它们返回异常码 01")
    args = parser.parse_args()

    pymodbus_apply_logging_config("ERROR")
    rejected = {int(code) for code in args.reject.split(",") if code}
    devices = [device(1, rejected), device(2, rejected)]
    framer = FramerType.RTU if args.framer == "rtu" else FramerType.SOCKET

    if args.transport == "tcp":
        server = ModbusTcpServer(devices, framer=framer, address=("127.0.0.1", args.port), trace_pdu=trace_pdu)
    elif args.transport == "udp":
        server = ModbusUdpServer(devices, framer=framer, address=("127.0.0.1", args.port), trace_pdu=trace_pdu)
    else:
        server = ModbusSerialServer(devices, framer=framer, port=args.serial, baudrate=args.baud, trace_pdu=trace_pdu)

    await server.serve_forever(background=True)
    emit("READY")
    # 测试关闭标准输入即表示结束。
    await asyncio.get_running_loop().run_in_executor(None, sys.stdin.read)
    await server.shutdown()


if __name__ == "__main__":
    asyncio.run(main())
