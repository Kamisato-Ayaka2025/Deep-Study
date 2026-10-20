/* Study Harness P1 source bundle: a normal, well-behaved C program.
 *
 * It exists to prove the positive half of the hard gate -- that a program can be compiled
 * and run inside the container at all, with a toolchain that has to read headers and
 * libraries outside the run directory to do it. It only writes inside its own directory.
 */
#include <stdio.h>

int main(void) {
    printf("study-harness-isolated-hello\n");
    return 0;
}
